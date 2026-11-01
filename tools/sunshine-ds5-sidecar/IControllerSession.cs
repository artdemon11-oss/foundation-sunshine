namespace Sunshine.Ds5Sidecar;

/// <summary>One client-owned virtual controller, addressed by its SDS5 device id.</summary>
internal interface IControllerSession : IDisposable
{
    byte DeviceId { get; }
    byte ClientControllerNumber { get; }
    bool HasAudio { get; }

    void SubmitInput(ReadOnlySpan<byte> payload);
    void SubmitTouch(ReadOnlySpan<byte> payload);
    void SubmitMotion(ReadOnlySpan<byte> payload);
    void SubmitBattery(ReadOnlySpan<byte> payload);
    void StartDefaultAudioEndpointGuard();
}
