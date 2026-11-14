// Orcub native haptics for iOS (Taptic Engine via UIFeedbackGenerator).
// Called from Orcub.Game.Haptics through [DllImport("__Internal")]. Always invoked on Unity's main thread.
#import <UIKit/UIKit.h>

static UIImpactFeedbackGenerator *gImpact[5];      // light, medium, heavy, soft, rigid
static UISelectionFeedbackGenerator *gSelection;
static UINotificationFeedbackGenerator *gNotify;

static UIImpactFeedbackGenerator *ImpactFor(int style)
{
    if (style < 0 || style > 4) style = 1;
    if (gImpact[style] == nil)
    {
        UIImpactFeedbackStyle s = UIImpactFeedbackStyleMedium;
        switch (style)
        {
            case 0: s = UIImpactFeedbackStyleLight; break;
            case 1: s = UIImpactFeedbackStyleMedium; break;
            case 2: s = UIImpactFeedbackStyleHeavy; break;
            case 3: s = UIImpactFeedbackStyleSoft; break;
            case 4: s = UIImpactFeedbackStyleRigid; break;
        }
        gImpact[style] = [[UIImpactFeedbackGenerator alloc] initWithStyle:s];
    }
    return gImpact[style];
}

extern "C"
{
    void OrcubHaptics_Prepare()
    {
        for (int i = 0; i < 5; i++) [ImpactFor(i) prepare];
        if (gSelection == nil) gSelection = [[UISelectionFeedbackGenerator alloc] init];
        if (gNotify == nil) gNotify = [[UINotificationFeedbackGenerator alloc] init];
        [gSelection prepare];
        [gNotify prepare];
    }

    void OrcubHaptics_Impact(int style, float intensity)
    {
        UIImpactFeedbackGenerator *g = ImpactFor(style);
        [g impactOccurredWithIntensity:(CGFloat)fmaxf(0.f, fminf(1.f, intensity))];
        [g prepare];
    }

    void OrcubHaptics_Selection()
    {
        if (gSelection == nil) gSelection = [[UISelectionFeedbackGenerator alloc] init];
        [gSelection selectionChanged];
        [gSelection prepare];
    }

    // 0 = success, 1 = warning, 2 = error
    void OrcubHaptics_Notify(int type)
    {
        if (gNotify == nil) gNotify = [[UINotificationFeedbackGenerator alloc] init];
        UINotificationFeedbackType t = type == 0 ? UINotificationFeedbackTypeSuccess
                                     : type == 1 ? UINotificationFeedbackTypeWarning
                                                 : UINotificationFeedbackTypeError;
        [gNotify notificationOccurred:t];
        [gNotify prepare];
    }
}
