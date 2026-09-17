#import <AppKit/AppKit.h>
#import <ServiceManagement/ServiceManagement.h>
#import <objc/runtime.h>
#import <mach-o/dyld.h>
#include <limits.h>
#include <stdlib.h>
#include <unistd.h>

typedef void (*SefirahLifecycleCallback)(void);

static SefirahLifecycleCallback gReopenCallback;
static SefirahLifecycleCallback gStatusItemCallback;
static NSStatusItem *gStatusItem;

@interface SefirahStatusItemTarget : NSObject
- (void)showMainWindow:(id)sender;
@end

@implementation SefirahStatusItemTarget
- (void)showMainWindow:(id)sender
{
    (void)sender;
    if (gStatusItemCallback != NULL) {
        gStatusItemCallback();
    }
}
@end

static SefirahStatusItemTarget *gStatusItemTarget;

static NSWindow *SefirahMainWindow(void)
{
    Class unoWindowClass = NSClassFromString(@"UNOWindow");
    for (NSWindow *window in NSApp.windows) {
        if (unoWindowClass != Nil && [window isKindOfClass:unoWindowClass]) {
            return window;
        }
    }

    for (NSWindow *window in NSApp.windows) {
        if ([window.title isEqualToString:@"Sefirah"]) {
            return window;
        }
    }

    return nil;
}

static void SefirahRunOnMainThread(dispatch_block_t block)
{
    if (NSThread.isMainThread) {
        block();
    } else {
        dispatch_async(dispatch_get_main_queue(), block);
    }
}

static BOOL SefirahApplicationShouldHandleReopen(
    id self,
    SEL command,
    NSApplication *application,
    BOOL hasVisibleWindows)
{
    (void)self;
    (void)command;
    (void)application;
    (void)hasVisibleWindows;
    if (gReopenCallback != NULL) {
        gReopenCallback();
    }
    return YES;
}

int sefirah_macos_install_reopen_handler(SefirahLifecycleCallback callback)
{
    gReopenCallback = callback;

    Class delegateClass = NSClassFromString(@"UNOApplicationDelegate");
    if (delegateClass == Nil) {
        return 0;
    }

    SEL selector = @selector(applicationShouldHandleReopen:hasVisibleWindows:);
    if (class_getInstanceMethod(delegateClass, selector) != NULL) {
        return 2;
    }

    return class_addMethod(
        delegateClass,
        selector,
        (IMP)SefirahApplicationShouldHandleReopen,
        "B@:@B") ? 1 : 0;
}

int sefirah_macos_create_status_item(SefirahLifecycleCallback callback)
{
    __block int result = 0;
    void (^create)(void) = ^{
        gStatusItemCallback = callback;
        if (gStatusItem != nil) {
            result = 1;
            return;
        }

        gStatusItemTarget = [SefirahStatusItemTarget new];
        gStatusItem = [NSStatusBar.systemStatusBar statusItemWithLength:NSSquareStatusItemLength];
        NSStatusBarButton *button = gStatusItem.button;
        if (button == nil) {
            gStatusItem = nil;
            gStatusItemTarget = nil;
            return;
        }

        NSImage *image = [NSImage imageWithSystemSymbolName:@"bolt.horizontal.circle.fill"
                                  accessibilityDescription:@"Sefirah"];
        image.template = YES;
        button.image = image;
        button.toolTip = @"Sefirah";
        button.target = gStatusItemTarget;
        button.action = @selector(showMainWindow:);
        result = 1;
    };

    if (NSThread.isMainThread) {
        create();
    } else {
        dispatch_sync(dispatch_get_main_queue(), create);
    }
    return result;
}

void sefirah_macos_remove_status_item(void)
{
    SefirahRunOnMainThread(^{
        if (gStatusItem != nil) {
            [NSStatusBar.systemStatusBar removeStatusItem:gStatusItem];
        }
        gStatusItem = nil;
        gStatusItemTarget = nil;
        gStatusItemCallback = NULL;
    });
}

int sefirah_macos_main_window_is_visible(void)
{
    __block int result = 0;
    void (^inspect)(void) = ^{
        NSWindow *window = SefirahMainWindow();
        result = window != nil && window.visible && !window.miniaturized;
    };

    if (NSThread.isMainThread) {
        inspect();
    } else {
        dispatch_sync(dispatch_get_main_queue(), inspect);
    }
    return result;
}

void sefirah_macos_show_main_window(void)
{
    SefirahRunOnMainThread(^{
        NSWindow *window = SefirahMainWindow();
        if (window == nil) {
            return;
        }

        [NSApp unhide:nil];
        if (window.miniaturized) {
            [window deminiaturize:nil];
        }
        [window makeKeyAndOrderFront:nil];
        [window orderFrontRegardless];

#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"
        [NSApp activateIgnoringOtherApps:YES];
#pragma clang diagnostic pop
    });
}

void sefirah_macos_hide_main_window(void)
{
    SefirahRunOnMainThread(^{
        [SefirahMainWindow() orderOut:nil];
    });
}

static BOOL SefirahUnregisterIfPresent(SMAppService *service)
{
    if (service.status == SMAppServiceStatusNotRegistered ||
        service.status == SMAppServiceStatusNotFound) {
        return YES;
    }

    NSError *error = nil;
    if (![service unregisterAndReturnError:&error]) {
        NSLog(@"Sefirah failed to unregister a login service: %@", error.localizedDescription);
        return NO;
    }
    return YES;
}

static int SefirahRunLaunchctl(NSArray<NSString *> *arguments)
{
    NSTask *task = [NSTask new];
    task.executableURL = [NSURL fileURLWithPath:@"/bin/launchctl"];
    task.arguments = arguments;
    task.standardOutput = NSFileHandle.fileHandleWithNullDevice;
    task.standardError = NSFileHandle.fileHandleWithNullDevice;

    NSError *error = nil;
    if (![task launchAndReturnError:&error]) {
        NSLog(@"Sefirah could not launch launchctl: %@", error.localizedDescription);
        return -1;
    }
    [task waitUntilExit];
    return task.terminationStatus;
}

static NSString *SefirahLaunchAgentDomain(void)
{
    return [NSString stringWithFormat:@"gui/%u", getuid()];
}

static NSString *SefirahLaunchAgentPath(void)
{
    return [NSHomeDirectory() stringByAppendingPathComponent:
        @"Library/LaunchAgents/com.castle.sefirah.login.plist"];
}

static NSString *SefirahApplicationBundlePath(void)
{
    char executablePath[PATH_MAX];
    uint32_t size = sizeof executablePath;
    if (_NSGetExecutablePath(executablePath, &size) == 0) {
        char canonicalPath[PATH_MAX];
        if (realpath(executablePath, canonicalPath) != NULL) {
            NSURL *url = [NSURL fileURLWithPath:[NSString stringWithUTF8String:canonicalPath]];
            for (int component = 0; component < 4; component++) {
                url = url.URLByDeletingLastPathComponent;
            }
            if ([url.pathExtension caseInsensitiveCompare:@"app"] == NSOrderedSame) {
                return url.path;
            }
        }
    }
    return NSBundle.mainBundle.bundlePath;
}

static BOOL SefirahWriteLegacyLaunchAgent(NSError **error)
{
    NSString *bundlePath = SefirahApplicationBundlePath();
    NSString *launcherPath = [bundlePath stringByAppendingPathComponent:
        @"Contents/Library/LaunchServices/SefirahLoginLauncher"];
    if (![NSFileManager.defaultManager isExecutableFileAtPath:launcherPath]) {
        if (error != NULL) {
            *error = [NSError errorWithDomain:@"com.castle.sefirah.lifecycle"
                                         code:1
                                     userInfo:@{NSLocalizedDescriptionKey:
                                         @"The bundled login launcher is missing or not executable."}];
        }
        return NO;
    }

    NSDictionary *propertyList = @{
        @"Label": @"com.castle.sefirah.login",
        @"ProgramArguments": @[launcherPath],
        @"LimitLoadToSessionType": @"Aqua",
        @"ProcessType": @"Interactive",
        @"RunAtLoad": @YES
    };
    NSData *data = [NSPropertyListSerialization dataWithPropertyList:propertyList
                                                               format:NSPropertyListXMLFormat_v1_0
                                                              options:0
                                                                error:error];
    if (data == nil) {
        return NO;
    }

    NSString *agentPath = SefirahLaunchAgentPath();
    NSString *agentDirectory = agentPath.stringByDeletingLastPathComponent;
    if (![NSFileManager.defaultManager createDirectoryAtPath:agentDirectory
                                 withIntermediateDirectories:YES
                                                  attributes:nil
                                                       error:error]) {
        return NO;
    }
    return [data writeToFile:agentPath options:NSDataWritingAtomic error:error];
}

int sefirah_macos_reconcile_launch_at_login(int enable)
{
    NSString *domain = SefirahLaunchAgentDomain();
    NSString *serviceTarget = [domain stringByAppendingString:@"/com.castle.sefirah.login"];
    NSString *agentPath = SefirahLaunchAgentPath();

    if (enable != 0) {
        NSError *writeError = nil;
        if (!SefirahWriteLegacyLaunchAgent(&writeError)) {
            NSLog(@"Sefirah failed to write its login agent: %@", writeError.localizedDescription);
            return -20;
        }

        (void)SefirahRunLaunchctl(@[@"enable", serviceTarget]);
        if (SefirahRunLaunchctl(@[@"print", serviceTarget]) != 0) {
            int bootstrapResult = SefirahRunLaunchctl(@[@"bootstrap", domain, agentPath]);
            if (bootstrapResult != 0) {
                return -100 - bootstrapResult;
            }
        }

        if (@available(macOS 13.0, *)) {
            SMAppService *mainApp = SMAppService.mainAppService;
            if (mainApp.status == SMAppServiceStatusEnabled) {
                NSError *unregisterError = nil;
                if (![mainApp unregisterAndReturnError:&unregisterError]) {
                    NSLog(@"Sefirah could not remove the old main-app login item: %@",
                          unregisterError.localizedDescription);
                    return -2000 - (int)unregisterError.code;
                }
            }
        }
        return 1;
    }

    (void)SefirahRunLaunchctl(@[@"bootout", serviceTarget]);
    NSError *removeError = nil;
    if ([NSFileManager.defaultManager fileExistsAtPath:agentPath] &&
        ![NSFileManager.defaultManager removeItemAtPath:agentPath error:&removeError]) {
        NSLog(@"Sefirah failed to remove its login agent: %@", removeError.localizedDescription);
        return -21;
    }

    if (@available(macOS 13.0, *)) {
        SMAppService *mainApp = SMAppService.mainAppService;
        if (!SefirahUnregisterIfPresent(mainApp)) {
            return -22;
        }
    }
    return 0;
}
