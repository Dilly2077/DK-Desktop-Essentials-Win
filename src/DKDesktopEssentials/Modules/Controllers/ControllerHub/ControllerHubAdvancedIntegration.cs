namespace DKDesktopEssentials.Modules.Controllers.ControllerHub;

/// <summary>Optional advanced controller bridge. Unsupported sensors/effects are never synthesized.</summary>
public sealed partial class ControllerHubService
{
    public IReadOnlyList<AdvancedControllerCapabilities> ListAdvancedCapabilities() =>
        _advancedFeatures.Value.ListCapabilities();

    public AdvancedControllerCapabilities GetAdvancedCapabilities(string deviceId) =>
        _advancedFeatures.Value.GetCapabilities(deviceId);

    public bool TryReadControllerMotion(string deviceId, out ControllerMotionReading? reading) =>
        _advancedFeatures.Value.TryReadMotion(deviceId, out reading);

    public bool TryReadControllerTouchpad(string deviceId, out ControllerTouchpadReading? reading) =>
        _advancedFeatures.Value.TryReadTouchpad(deviceId, out reading);

    public ValueTask<ControllerFeedbackResult> SetControllerFeedbackAsync(
        string deviceId, ControllerFeedbackRequest request,
        CancellationToken cancellationToken = default) =>
        _advancedFeatures.Value.SetFeedbackAsync(deviceId, request, cancellationToken);

    public ValueTask<ControllerFeedbackResult> StopControllerFeedbackAsync(string deviceId) =>
        _advancedFeatures.Value.StopFeedbackAsync(deviceId);
}
