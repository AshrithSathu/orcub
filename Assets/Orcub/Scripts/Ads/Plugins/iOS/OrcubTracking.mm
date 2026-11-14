#import <AppTrackingTransparency/AppTrackingTransparency.h>
#import <UIKit/UIKit.h>
extern "C" int OrcubTrackingStatus() {
    return (int)[ATTrackingManager trackingAuthorizationStatus];
}
extern "C" void OrcubRequestTracking() {
    dispatch_async(dispatch_get_main_queue(), ^{
        if ([UIApplication sharedApplication].applicationState == UIApplicationStateActive &&
            [ATTrackingManager trackingAuthorizationStatus] == ATTrackingManagerAuthorizationStatusNotDetermined) {
            [ATTrackingManager requestTrackingAuthorizationWithCompletionHandler:^(ATTrackingManagerAuthorizationStatus status) {}];
        }
    });
}
