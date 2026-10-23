namespace FerretSharp.UI.State;

/// <summary>The zoom of a diagram (relations WP-46, records WP-60): fixed steps from 50 % to 150 %. Only in memory.</summary>
public sealed class DiagramZoom
{
    private static readonly double[] Steps = [0.5, 0.67, 0.8, 0.9, 1, 1.1, 1.25, 1.5];

    public double Value { get; private set; } = 1;

    public bool CanZoomIn => Value < Steps[^1];

    public bool CanZoomOut => Value > Steps[0];

    public void ZoomIn() => Value = Steps.FirstOrDefault(z => z > Value, Steps[^1]);

    public void ZoomOut() => Value = Steps.LastOrDefault(z => z < Value, Steps[0]);

    public void Reset() => Value = 1;
}
