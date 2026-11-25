using System.Text;
using AgenticWorkflow.Execution.Local;
using AgenticWorkflow.Runtime.Herdr.ClaudeEvidence;

// The one executable AgenticWorkflow gives a native Claude Code session. Started by the harness in one of two ways:
//
//  - as a HOOK (PostToolUse / PostToolUseFailure / PostToolBatch of an AgenticWorkflow-owned session): observational, it records evidence in the controller's
//    channel (argument 0) and always exits 0. The one thing it ever prints is the harness's "stop this turn" answer, after it has durably recorded the
//    question of a session that asked one.
//  - as the workflow MCP SERVER (first argument "--mcp", then the channel directory): the stdio server through which the agent asks the workflow a question or
//    proposes its WHAT, with exactly the tools the channel's configuration says the session was given.
var utf8 = new UTF8Encoding(false);

if (args.Length > 0 && args[0] == ClaudeClarificationServer.ModeArgument)
{
    using var input = new StreamReader(Console.OpenStandardInput(), utf8);
    using var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };

    return await ClaudeClarificationServer.RunAsync(input, output, args.Length > 1 ? args[1] : null);
}

using var stdin = new StreamReader(Console.OpenStandardInput(), utf8);

return await ClaudeHookHandler.RunAsync(args, stdin, new GitImplementationStateProvider());
