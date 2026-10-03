#import <AppKit/AppKit.h>
#import <UserNotifications/UserNotifications.h>

typedef void (*SefirahNotificationCompletion)(long long, int, const char *);
typedef void (*SefirahNotificationAction)(const char *);
static SefirahNotificationCompletion gCompletion;
static SefirahNotificationAction gAction;
static UNUserNotificationCenter *gCenter;
static NSMutableDictionary<NSString *, UNNotificationCategory *> *gCategories;

static void Complete(long long request, NSError *error)
{
    if (gCompletion != NULL) gCompletion(request, error == nil ? 0 : 1,
        error.localizedDescription.UTF8String ?: "");
}

@interface SefirahNotificationDelegate : NSObject <UNUserNotificationCenterDelegate>
@end
@implementation SefirahNotificationDelegate
- (void)userNotificationCenter:(UNUserNotificationCenter *)center
    willPresentNotification:(UNNotification *)notification
    withCompletionHandler:(void (^)(UNNotificationPresentationOptions))completion
{
    (void)center; (void)notification;
    completion(UNNotificationPresentationOptionBanner | UNNotificationPresentationOptionList);
}
- (void)userNotificationCenter:(UNUserNotificationCenter *)center
    didReceiveNotificationResponse:(UNNotificationResponse *)response
    withCompletionHandler:(void (^)(void))completion
{
    (void)center;
    if (![response.actionIdentifier isEqualToString:UNNotificationDismissActionIdentifier]) {
        NSMutableDictionary *data = [response.notification.request.content.userInfo mutableCopy];
        data[@"action"] = [response.actionIdentifier isEqualToString:UNNotificationDefaultActionIdentifier]
            ? @"open" : response.actionIdentifier;
        if ([response isKindOfClass:UNTextInputNotificationResponse.class]) {
            data[@"reply"] = ((UNTextInputNotificationResponse *)response).userText;
        }
        NSData *json = [NSJSONSerialization dataWithJSONObject:data options:0 error:nil];
        NSString *text = [[NSString alloc] initWithData:json encoding:NSUTF8StringEncoding];
        if (gAction != NULL && text != nil) gAction(text.UTF8String);
    }
    completion();
}
@end
static SefirahNotificationDelegate *gDelegate;

int sefirah_macos_initialize_notifications(SefirahNotificationCompletion completion,
    SefirahNotificationAction action)
{
    if (![NSBundle.mainBundle.bundleIdentifier isEqualToString:@"com.castle.sefirah"]) return -1;
    gCompletion = completion;
    gAction = action;
    gCenter = UNUserNotificationCenter.currentNotificationCenter;
    gCategories = [NSMutableDictionary new];
    gDelegate = [SefirahNotificationDelegate new];
    gCenter.delegate = gDelegate;
    return 0;
}

void sefirah_macos_authorize_notifications(long long request)
{
    [gCenter requestAuthorizationWithOptions:UNAuthorizationOptionAlert | UNAuthorizationOptionSound
        completionHandler:^(BOOL granted, NSError *error) {
            if (!granted && error == nil) {
                error = [NSError errorWithDomain:@"com.castle.sefirah.notifications" code:1
                    userInfo:@{NSLocalizedDescriptionKey: @"Notifications are disabled in System Settings."}];
            }
            Complete(request, error);
        }];
}

void sefirah_macos_show_notification(const char *json, long long request)
{
    NSData *bytes = [[NSString stringWithUTF8String:json] dataUsingEncoding:NSUTF8StringEncoding];
    NSError *error = nil;
    NSDictionary *data = [NSJSONSerialization JSONObjectWithData:bytes options:0 error:&error];
    if (data == nil) { Complete(request, error); return; }
    dispatch_async(dispatch_get_main_queue(), ^{
        UNMutableNotificationContent *content = [UNMutableNotificationContent new];
        content.title = data[@"title"] ?: @"Sefirah";
        content.subtitle = data[@"subtitle"] ?: @"";
        content.body = data[@"body"] ?: @"";
        content.threadIdentifier = data[@"group"] ?: @"";
        content.userInfo = data[@"context"] ?: @{};
        NSArray *buttons = data[@"buttons"];
        if (buttons.count > 0) {
            NSMutableArray *actions = [NSMutableArray new];
            for (NSDictionary *button in buttons) {
                UNNotificationAction *action;
                if ([button[@"id"] isEqualToString:@"reply"]) {
                    action = [UNTextInputNotificationAction actionWithIdentifier:button[@"id"]
                        title:button[@"label"] options:0 textInputButtonTitle:button[@"label"]
                        textInputPlaceholder:@""];
                } else {
                    action = [UNNotificationAction actionWithIdentifier:button[@"id"]
                        title:button[@"label"] options:0];
                }
                [actions addObject:action];
            }
            NSString *category = data[@"category"];
            gCategories[category] = [UNNotificationCategory categoryWithIdentifier:category
                actions:actions intentIdentifiers:@[] options:0];
            [gCenter setNotificationCategories:[NSSet setWithArray:gCategories.allValues]];
            content.categoryIdentifier = category;
        }
        UNNotificationRequest *notification = [UNNotificationRequest
            requestWithIdentifier:data[@"id"] content:content trigger:nil];
        [gCenter addNotificationRequest:notification withCompletionHandler:^(NSError *addError) {
            Complete(request, addError);
        }];
    });
}

static BOOL Matches(UNNotificationRequest *notification, NSDictionary *query)
{
    NSDictionary *context = notification.content.userInfo;
    return (query[@"tag"] == nil || [context[@"tag"] isEqual:query[@"tag"]]) &&
        (query[@"group"] == nil || [context[@"group"] isEqual:query[@"group"]]);
}

void sefirah_macos_remove_notifications(const char *json, long long request)
{
    NSData *bytes = [[NSString stringWithUTF8String:json] dataUsingEncoding:NSUTF8StringEncoding];
    NSError *error = nil;
    NSDictionary *query = [NSJSONSerialization JSONObjectWithData:bytes options:0 error:&error];
    if (query == nil) { Complete(request, error); return; }
    dispatch_group_t group = dispatch_group_create();
    dispatch_group_enter(group);
    [gCenter getPendingNotificationRequestsWithCompletionHandler:^(NSArray<UNNotificationRequest *> *pending) {
        NSMutableArray *ids = [NSMutableArray new];
        for (UNNotificationRequest *notification in pending) {
            if (Matches(notification, query)) [ids addObject:notification.identifier];
        }
        [gCenter removePendingNotificationRequestsWithIdentifiers:ids];
        dispatch_group_leave(group);
    }];
    dispatch_group_enter(group);
    [gCenter getDeliveredNotificationsWithCompletionHandler:^(NSArray<UNNotification *> *delivered) {
        NSMutableArray *ids = [NSMutableArray new];
        for (UNNotification *notification in delivered) {
            if (Matches(notification.request, query)) [ids addObject:notification.request.identifier];
        }
        [gCenter removeDeliveredNotificationsWithIdentifiers:ids];
        dispatch_group_leave(group);
    }];
    dispatch_group_notify(group, dispatch_get_main_queue(), ^{ Complete(request, nil); });
}
