#import <Foundation/Foundation.h>
#import "UnityAppController.h"
#import <GoogleSignIn/GoogleSignIn.h>
extern "C" void OrcubGoogleDisconnect() {
    [[GIDSignIn sharedInstance] disconnectWithCompletion:^(NSError *error) {
        UnitySendMessage("ClientServices", "GoogleDisconnected", error ? "failed" : "");
    }];
}
extern "C" void OrcubClearPreferences() {
    NSUserDefaults *prefs = [NSUserDefaults standardUserDefaults];
    for (NSString *key in prefs.dictionaryRepresentation) if ([key hasPrefix:@"orcub."]) [prefs removeObjectForKey:key];
    [prefs synchronize];
}
