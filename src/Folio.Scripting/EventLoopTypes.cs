namespace Folio.Scripting;

/// <summary>
/// Task sources defined by HTML §8.1.7:
/// DOM manipulation, user interaction, networking, timers, and rendering.
/// </summary>
public enum TaskSource
{
    UserInteraction,
    DomManipulation,
    Timer,
    Networking,
    Rendering,
    Generic,
}

/// <summary>
/// A runnable task in the event loop task queues.
/// </summary>
public sealed class EventLoopTask
{
    public EventLoopTask(TaskSource source, Action action, long sequenceNumber, string? description = null)
    {
        Source = source;
        Action = action;
        SequenceNumber = sequenceNumber;
        Description = description ?? source.ToString();
    }

    public TaskSource Source { get; }
    public Action Action { get; }
    public long SequenceNumber { get; }
    public string Description { get; }
}

/// <summary>
/// Callbacks and hooks for "Update the rendering" steps (HTML §8.1.7.3).
/// Later issues (#459–#465) fill in geometry, observers, and relayout hooks.
/// </summary>
public interface IRenderingStepsHooks
{
    void RunResizeSteps();
    void RunScrollSteps();
    void RunMediaQueryChanges();
    void RunAnimationFrameCallbacks(double timestamp);
    void RunResizeObservers();
    void RunIntersectionObservers();
    void RunUpdateStyleAndLayout();
    void RunPaint();
}

/// <summary>
/// Default no-op rendering steps implementation that can be overridden or hooked.
/// </summary>
public class RenderingStepsHooks : IRenderingStepsHooks
{
    public virtual void RunResizeSteps() { }
    public virtual void RunScrollSteps() { }
    public virtual void RunMediaQueryChanges() { }
    public virtual void RunAnimationFrameCallbacks(double timestamp) { }
    public virtual void RunResizeObservers() { }
    public virtual void RunIntersectionObservers() { }
    public virtual void RunUpdateStyleAndLayout() { }
    public virtual void RunPaint() { }
}
