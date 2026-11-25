using AgenticWorkflow.Cli;

// Composition root of the product. The first Ctrl+C asks the run to stop cleanly (the agent session this run started is stopped, the
// isolated Herdr core is released, the outcome is recorded); a second one ends the process immediately.
using var interrupt = new CancellationTokenSource();
var presses = 0;

Console.CancelKeyPress += (_, e) =>
{
    if (Interlocked.Increment(ref presses) > 1)
        return;

    e.Cancel = true;
    Console.Error.WriteLine("Interrupted: stopping the agent session and cleaning up (press Ctrl+C again to exit immediately).");

    try
    {
        interrupt.Cancel();
    }
    catch (ObjectDisposedException)
    {
        // the app already finished
    }
};

return await new CliApp(CliServices.Production()).RunAsync(args, interrupt.Token);
