#import <AppKit/AppKit.h>
#import <mach-o/dyld.h>
#include <limits.h>
#include <stdlib.h>

static NSURL *SefirahApplicationURL(void)
{
    char executablePath[PATH_MAX];
    uint32_t size = sizeof executablePath;
    if (_NSGetExecutablePath(executablePath, &size) != 0) {
        return nil;
    }

    char canonicalPath[PATH_MAX];
    if (realpath(executablePath, canonicalPath) == NULL) {
        return nil;
    }

    NSURL *url = [NSURL fileURLWithPath:[NSString stringWithUTF8String:canonicalPath]];
    for (int component = 0; component < 4; component++) {
        url = url.URLByDeletingLastPathComponent;
    }
    return url;
}

int main(void)
{
    @autoreleasepool {
        NSURL *applicationURL = SefirahApplicationURL();
        if (applicationURL == nil) {
            NSLog(@"Sefirah login launcher could not resolve the application bundle.");
            return 1;
        }

        NSWorkspaceOpenConfiguration *configuration = [NSWorkspaceOpenConfiguration configuration];
        configuration.arguments = @[@"--login-startup"];
        configuration.activates = NO;

        __block BOOL completed = NO;
        __block NSError *launchError = nil;
        [NSWorkspace.sharedWorkspace
            openApplicationAtURL:applicationURL
            configuration:configuration
            completionHandler:^(NSRunningApplication *application, NSError *error) {
                (void)application;
                launchError = error;
                completed = YES;
            }];

        NSDate *deadline = [NSDate dateWithTimeIntervalSinceNow:15.0];
        while (!completed && deadline.timeIntervalSinceNow > 0) {
            [NSRunLoop.currentRunLoop runMode:NSDefaultRunLoopMode
                                   beforeDate:[NSDate dateWithTimeIntervalSinceNow:0.1]];
        }

        if (!completed || launchError != nil) {
            NSLog(@"Sefirah login launcher failed: %@", launchError.localizedDescription ?: @"timed out");
            return 2;
        }
        return 0;
    }
}
