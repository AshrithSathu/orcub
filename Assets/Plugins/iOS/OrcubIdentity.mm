#import <Foundation/Foundation.h>
#import <Security/Security.h>
#import <GoogleSignIn/GoogleSignIn.h>
#import "UnityAppController.h"
extern "C" char *OrcubSecretRead() {
    NSDictionary *q = @{(__bridge id)kSecClass:(__bridge id)kSecClassGenericPassword,
      (__bridge id)kSecAttrService:@"com.orcub.puzzle.install", (__bridge id)kSecReturnData:@YES};
    CFTypeRef result = NULL;
    if (SecItemCopyMatching((__bridge CFDictionaryRef)q, &result) != errSecSuccess) return strdup("");
    NSData *data = CFBridgingRelease(result);
    return strdup([[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding].UTF8String ?: "");
}
extern "C" bool OrcubSecretWrite(const char *value) {
    NSDictionary *q = @{(__bridge id)kSecClass:(__bridge id)kSecClassGenericPassword,
      (__bridge id)kSecAttrService:@"com.orcub.puzzle.install"};
    SecItemDelete((__bridge CFDictionaryRef)q);
    if (!value || !strlen(value)) return true;
    NSMutableDictionary *item = [q mutableCopy];
    item[(__bridge id)kSecValueData] = [[NSString stringWithUTF8String:value] dataUsingEncoding:NSUTF8StringEncoding];
    item[(__bridge id)kSecAttrAccessible] = (__bridge id)kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly;
    return SecItemAdd((__bridge CFDictionaryRef)item, NULL) == errSecSuccess;
}
extern "C" void OrcubGoogleSignIn() {
    dispatch_async(dispatch_get_main_queue(), ^{
        NSString *path = [[NSBundle mainBundle] pathForResource:@"GoogleService-Info" ofType:@"plist"];
        NSDictionary *settings = [NSDictionary dictionaryWithContentsOfFile:path];
        [GIDSignIn sharedInstance].configuration = [[GIDConfiguration alloc] initWithClientID:settings[@"CLIENT_ID"]];
        [[GIDSignIn sharedInstance] signInWithPresentingViewController:UnityGetGLViewController() completion:^(GIDSignInResult *result, NSError *error) {
            UnitySendMessage("ClientServices", "GoogleResult", error ? "" : result.user.idToken.tokenString.UTF8String);
        }];
    });
}
extern "C" void OrcubGoogleSignOut() { [[GIDSignIn sharedInstance] signOut]; }
@interface OrcubAppController : UnityAppController
@end
@implementation OrcubAppController
- (BOOL)application:(UIApplication *)app openURL:(NSURL *)url options:(NSDictionary<UIApplicationOpenURLOptionsKey,id> *)options {
    if ([[GIDSignIn sharedInstance] handleURL:url]) return YES;
    return [super application:app openURL:url options:options];
}
@end
IMPL_APP_CONTROLLER_SUBCLASS(OrcubAppController)
