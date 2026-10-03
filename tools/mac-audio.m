#import <Foundation/Foundation.h>
#import <CoreAudio/CoreAudio.h>

typedef void (*SefirahAudioCallback)(void);
static SefirahAudioCallback gAudioCallback;
static AudioDeviceID gOutput = kAudioObjectUnknown;
static BOOL gMonitoring;
static const AudioObjectPropertyAddress gDefaultOutput = {
    kAudioHardwarePropertyDefaultOutputDevice, kAudioObjectPropertyScopeGlobal,
    kAudioObjectPropertyElementMain
};
static const AudioObjectPropertyAddress gOutputProperties[] = {
    {kAudioDevicePropertyVolumeScalar, kAudioObjectPropertyScopeOutput, 0},
    {kAudioDevicePropertyVolumeScalar, kAudioObjectPropertyScopeOutput, 1},
    {kAudioDevicePropertyVolumeScalar, kAudioObjectPropertyScopeOutput, 2},
    {kAudioDevicePropertyMute, kAudioObjectPropertyScopeOutput, 0},
    {kAudioDevicePropertyMute, kAudioObjectPropertyScopeOutput, 1},
    {kAudioDevicePropertyMute, kAudioObjectPropertyScopeOutput, 2}
};

static OSStatus SefirahAudioChanged(AudioObjectID object, UInt32 count,
    const AudioObjectPropertyAddress *addresses, void *context);

// All listener registration and managed callbacks run on the main queue. CoreAudio
// can call its listener on other threads, including after removal has started.
static void SefirahObserveOutput(BOOL add)
{
    if (gOutput == kAudioObjectUnknown) return;
    for (size_t i = 0; i < sizeof gOutputProperties / sizeof *gOutputProperties; i++) {
        const AudioObjectPropertyAddress *address = &gOutputProperties[i];
        if (add && AudioObjectHasProperty(gOutput, address)) {
            AudioObjectAddPropertyListener(gOutput, address, SefirahAudioChanged, NULL);
        } else if (!add) {
            AudioObjectRemovePropertyListener(gOutput, address, SefirahAudioChanged, NULL);
        }
    }
}

static void SefirahRefreshOutput(void)
{
    AudioDeviceID output = kAudioObjectUnknown;
    UInt32 size = sizeof output;
    if (AudioObjectGetPropertyData(kAudioObjectSystemObject, &gDefaultOutput,
            0, NULL, &size, &output) != noErr) output = kAudioObjectUnknown;
    if (output == gOutput) return;
    SefirahObserveOutput(NO);
    gOutput = output;
    SefirahObserveOutput(YES);
}

static OSStatus SefirahAudioChanged(AudioObjectID object, UInt32 count,
    const AudioObjectPropertyAddress *addresses, void *context)
{
    (void)object; (void)count; (void)addresses; (void)context;
    dispatch_async(dispatch_get_main_queue(), ^{
        if (!gMonitoring) return;
        SefirahRefreshOutput();
        if (gAudioCallback != NULL) gAudioCallback();
    });
    return noErr;
}

int sefirah_macos_monitor_audio(SefirahAudioCallback callback)
{
    __block OSStatus status = noErr;
    void (^start)(void) = ^{
        gAudioCallback = callback;
        if (gMonitoring) return;
        status = AudioObjectAddPropertyListener(kAudioObjectSystemObject,
            &gDefaultOutput, SefirahAudioChanged, NULL);
        if (status != noErr) return;
        gMonitoring = YES;
        SefirahRefreshOutput();
    };
    if (NSThread.isMainThread) start();
    else dispatch_sync(dispatch_get_main_queue(), start);
    return (int)status;
}

void sefirah_macos_stop_audio_monitor(void)
{
    void (^stop)(void) = ^{
        gAudioCallback = NULL;
        if (!gMonitoring) return;
        gMonitoring = NO;
        AudioObjectRemovePropertyListener(kAudioObjectSystemObject,
            &gDefaultOutput, SefirahAudioChanged, NULL);
        SefirahObserveOutput(NO);
        gOutput = kAudioObjectUnknown;
    };
    if (NSThread.isMainThread) stop();
    else dispatch_sync(dispatch_get_main_queue(), stop);
}
