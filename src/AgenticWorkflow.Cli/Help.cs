namespace AgenticWorkflow.Cli;

internal static class Help
{
    public static string For(string? command) => command switch
    {
        "run" => Run,
        "status" => Status,
        "show" => Show,
        "runs" => Runs,
        "abort" => Abort,
        "accept" => Accept,
        "approve" => Approve,
        "request-changes" => RequestChanges,
        "continue" => Continue,
        "bind" => Bind,
        "answer" => Answer,
        "ctl" => Ctl,
        "project" => Project,
        "attach" => Attach,
        "doctor" => Doctor,
        _ => Overview,
    };

    private const string Overview = """
        aw - run one bounded task through a native coding agent, with controller-owned verification and, when you ask for it, a final human ACCEPT.

        Usage:
          aw run [--workspace <dir>] [--config <file>] [--acceptance none|human] [--require what|how|review|prove|what,how,review,prove] [--interactive | --non-interactive] "<task>"
          aw run [--workspace <dir>] [--config <file>] [--acceptance none|human] [--require what] [--interactive | --non-interactive] --task-file <file>
          aw runs [--limit <n>]
          aw status <run-id> [--json]
          aw accept <run-id> [--gate <gate-id>]
          aw approve <run-id> --gate <gate-id> [--interactive | --non-interactive]
          aw request-changes <run-id> --feedback "<what to change>" [--gate <gate-id>] [--interactive | --non-interactive]
          aw answer <run-id> --question <question-id> --text "<your answer>" [--interactive | --non-interactive]
          aw continue <run-id> [--interactive | --non-interactive]
          aw bind <run-id> --role <root|reviewer|prover> --profile <profile-id>
          aw abort <run-id>
          aw show [--workspace <dir>] [--config <file>]
          aw ctl health | tasks [--limit <n>] | task <task-id> | stop [--force] | send [--request-file <absolute path>]
          aw project inspect [--workspace <dir>] [--config <file>]
          aw project init --workspace <dir> --request-file <absolute path>
          aw attach <run-id> [--describe]
          aw doctor [--workspace <dir>] [--json]
          aw <run|approve|request-changes|answer|accept|abort|continue|bind> ... [--direct]
          aw --help | --version

        Commands:
          run              Start a run: agent works, the controller verifies, then the run completes (acceptance none, the default) or the ACCEPT gate opens (acceptance human).
          runs             List the most recent runs (id, state, start time, workspace, task) to find a run id again. Reads the durable state only.
          status           Replay a run's durable event stream and show its state and pending gate (works from any later process).
          accept           ACCEPT the verified result at the run's gate (acceptance human only); the run completes. No agent or model is involved.
          approve          APPROVE the agent's WHAT proposal (the requirements) or HOW proposal (the implementation strategy); the agent then continues in its own conversation. Completes nothing.
          request-changes  Do not accept / approve: close the gate, give feedback, and continue the work in the agent's own native conversation (resumed when possible).
          continue         Retry the work of a request-changes whose continuation was blocked by a runtime failure. No new decision, no new revision.
          bind             Bind a role the run needs (a reviewer or prover an agent asked for) to one of the project's profiles, recorded with the run. Starts nothing; then 'aw continue'.
          abort            Cancel a BLOCKED run, or stop at the ACCEPT gate without accepting.
          show             Resolve and validate the project configuration without starting an agent.
          project          Typed project operations for integrations (a Herdr mod): 'inspect' describes a workspace as JSON, 'init' creates its configuration from confirmed choices.
          attach           Attach to the agent's own Herdr session for a run (where its native permissions, trust and login questions are answered), or --describe whether there is one.
          doctor           Diagnose this installation and machine (read-only): version and files, state folder, controller, Git, Herdr, Claude Code, Pi, the cockpit and, inside a project, its profiles.
          ctl              Talk to the local AgenticWorkflow controller (started automatically): typed requests for integrations such as a Herdr mod.
        --direct         With a controller running, 'run', 'approve', 'request-changes', 'answer', 'accept', 'abort' and 'continue' are handed to it (it records the decision and works in the background;
                         follow with 'aw status'). --direct runs the command in this process instead, to its outcome: the administrative and recovery path. See 'aw ctl --help'.

        Exit codes: 0 completed (verified with no acceptance required, or accepted); 2 usage/configuration; 3 run not found; 4 run state unreadable; 11 startup blocked; 12 verification
        unresolved; 13 stale workflow state; 14 runtime error; 15 blocked, native interaction unavailable (nobody attending); 16 aborted; 17 verified,
        awaiting your ACCEPT (the normal end of a run with acceptance human); 18 accept / request-changes / answer / continue refused; 19 the agent asked a
        question and awaits your ANSWER; 20 the agent proposed the WHAT and awaits your APPROVAL; 21 the WHAT stage was violated (blocked);
        22 the agent proposed the HOW and awaits your APPROVAL; 23 the HOW stage was violated (blocked); 24 an independent REVIEW is required but no reviewer profile is bound (blocked); 25 the reviewer modified the workspace (blocked);
        26 the independent review still requires changes after the last turn; 27 formal PROVE is required but no prover profile is bound (blocked); 28 the prover modified the workspace (blocked);
        29 the proof still fails after the last turn; 30 PROVE is required but no Proof Contract was submitted (blocked); 33 doctor found a failing check; 130 cancelled. After an agent reclassification the WHAT/HOW
        proposal is revised and the run ends with exit 20 / 22 again ('aw status' shows 'reclassified').
        See docs/cli.md for the command reference and docs/concepts.md for the workflow.

        """;

    private const string Run = """
        Usage: aw run [--workspace <dir>] [--config <file>] [--acceptance none|human] [--require what] [--profile <id>] [--reviewer-profile <id>] [--prover-profile <id>] [--interactive | --non-interactive] "<task>"
               aw run [--workspace <dir>] [--config <file>] [--acceptance none|human] [--require what] [--profile <id>] [--reviewer-profile <id>] [--prover-profile <id>] [--interactive | --non-interactive] --task-file <file>

        Runs one bounded task in the configured workspace. The run id is printed first; use it with 'aw status'.
        Each role runs under a profile (a harness, a model and a reasoning level) that was named for it: --profile, --reviewer-profile and --prover-profile name one for the root,
        reviewer and prover roles, and a role you do not name uses the project's own binding ("profile", "reviewerProfile", "proverProfile"), if it has one. The resolved
        configuration is checked first (the profile exists, its harness can run it, its model and reasoning are accepted) and recorded with the run; nothing is substituted.
        Nothing is committed or pushed, and permission requests are never approved or denied on your behalf.

        Whether the run needs your final ACCEPT is your explicit choice, recorded with the run: --acceptance, else the configuration's "acceptance", else none.
        It is never inferred from the task.
          none   (default) When the controller's checks pass, the controller completes the run itself (exit 0). No gate, no human action. For objective,
                 bounded work that the checks prove.
          human  When the checks pass, the run does NOT complete: the workflow opens its own ACCEPT gate, becomes AWAITING_HUMAN, the agent session ends, and aw
                 exits with 17. The gate is durable; decide it later, from any process, with 'aw accept' or 'aw request-changes'. For work you want to review.
        Only a run with acceptance human can have changes requested: a run that completed without acceptance cannot be reopened.

        --require what makes the WHAT stage mandatory for this run: before anything is implemented, the agent must propose the requirements contract (objective,
        required behavior, scope, constraints, non-goals, acceptance conditions) and you must approve it with 'aw approve' (exit 20 when it is waiting). The choice is
        recorded with the run and cannot be removed later. Without it the agent may still propose a WHAT, but only when the outcome of the task is genuinely open;
        a simple task is never held up. Nothing is changed in the workspace before approval; if it is, the run is BLOCKED (exit 21).

        --require review makes an independent REVIEW mandatory: after the ordinary verification passes, a FRESH reviewer session (a new native conversation that never sees the
        implementer's transcript) reads the implementation against the approved task and reports PASS or CHANGES_REQUIRED with structured findings. It needs a reviewer
        profile: --reviewer-profile or 'reviewerProfile' in the project configuration (an explicit binding, never the implementer's profile by default; the same profile is allowed
        when you write it). Without one the run is refused before anything starts (a review an agent asks for mid-run waits for 'aw bind'). PASS lets the run complete (acceptance none) or open the ACCEPT gate (acceptance human); CHANGES_REQUIRED resumes the ORIGINAL implementer
        with the findings, then re-verifies and starts a NEW fresh reviewer. A review is judgment, not verification and not your ACCEPT. The reviewer is read-only: if it changes
        the workspace the run is BLOCKED and nothing is absorbed. REVIEW is independent of WHAT and HOW (--require what,review, how,review, what,how,review). The agent may also
        request a review itself; it can never remove the requirement.

        --require prove makes formal PROVE mandatory: the implementer must submit a structured Proof Contract (1-8 claims, each with an acceptance criterion and evidence
        requirements drawn from a closed vocabulary: verification:<check>, review:current-pass, observation:<description>); after verification passes (and the review passes,
        when required) a FRESH prover session reads the contract and a deterministic packet, never a transcript, and reports a verdict per claim (pass, fail, inconclusive) citing
        typed evidence. The overall verdict is derived. It needs a prover profile:
        --prover-profile or 'proverProfile' in the project configuration (an explicit binding; never the implementer's or the reviewer's profile by default). Without one the run
        is refused before anything starts (a proof an agent asks for mid-run waits for 'aw bind'). A pass lets the run complete or open the ACCEPT gate; a fail or
        inconclusive proof resumes the ORIGINAL implementer with a bounded delta, then re-verifies (and re-reviews) and starts a NEW fresh prover. The prover is read-only: if it
        changes the workspace the run is BLOCKED and nothing is absorbed. PROVE is independent of WHAT, HOW and REVIEW, requires none of them and implies none. The agent may also
        request PROVE itself by submitting a contract; it can never remove the requirement.

        --require how makes the HOW stage mandatory the same way: the agent must propose its implementation strategy (strategy, components, data or control flow,
        compatibility, risks and tradeoffs, verification approach) and you must approve it with 'aw approve' (exit 22 when it is waiting; a HOW stage violation is
        exit 23). --require what,how requires both, WHAT first: the HOW is only proposed after the WHAT is approved. Requiring one never implies the other. Without it
        the agent may still propose a HOW, but only for a consequential strategy with materially different alternatives; a simple task is never held up.

        Reclassification: if the agent finds concrete new evidence that invalidates a stage you already approved, it can report it. The approval is INVALIDATED (it stays
        in the history), the implementation and verification so far become stale, the workspace is kept as provisional work, and the run ends again at exit 20 (WHAT) or 22
        (HOW) with the revised proposal for you to approve. The agent can only raise rigor: nothing is lowered automatically.

        The agent is Claude Code, in its own Herdr session. When it needs a permission its own settings do not already
        grant, it asks you in ITS prompt, not in aw: aw records the wait, prints "NATIVE INTERACTION REQUIRED" with the PowerShell and
        Git Bash commands that attach to that session ('aw status <run-id>' shows them again while the run is live), and keeps waiting until the
        agent resumes. AGENTICWORKFLOW_NOTIFY=toast also shows a best-effort Windows notification. aw never learns what you chose. Operations the tool policy denies
        outright (git history changes, publishing, network fetches) are refused by the agent itself and never prompt.
        --interactive says a person is attending; --non-interactive says nobody is, so the run records AWAITING_HUMAN, stops the session, becomes BLOCKED and
        ends with exit 15 instead of waiting. With neither, a person is assumed only when input and output are a terminal.
        A run that stops otherwise cannot be resumed yet.

        """;

    private const string Status = """
        Usage: aw status <run-id> [--json]

        Replays the run's durable event stream and prints its workflow state, its acceptance requirement, revisions, verification and, when one is waiting,
        the pending ACCEPT gate with the exact commands that decide it.

        With --json, writes the run's structured read model instead: one JSON document on stdout and nothing else, the same facts as the text report plus the
        active stage, every gate with its binding and currentness, review and proof evidence, role bindings and the run's operational facts. A run that cannot be
        read is reported as a JSON document with an "error" object ({"schemaVersion", "error": {"code", "message"}}) and the same exit code as the text form (3: no
        such run, 4: unreadable state; 2: not a run id). A malformed command line is a usage error on stderr, as for every command.

        """;

    private const string Ctl = """
        Usage: aw ctl health [--no-start]
               aw ctl tasks [--limit <n>] [--no-start]
               aw ctl task <task-id> [--no-start]
               aw ctl stop [--force]
               aw ctl send                  (one typed request as JSON on standard input)
               aw ctl serve [--idle-timeout <seconds>]

        Sends one typed request to the local AgenticWorkflow controller and prints the reply as JSON. The controller is a per-user background process that you never start
        yourself: if none is running, 'aw ctl' starts one, waits (up to 20 seconds) until it answers, and then makes the request. Many simultaneous commands start exactly one.

          health   The controller's status, identity, version and the protocol versions it speaks.
          tasks    The most recent tasks (default 20, at most 200), newest first, each with its state, stage and workspace.
          task     One task's full structured read model: byte-for-byte what 'aw status <task-id> --json' prints.
          stop     Ask the controller to stop (it also stops by itself after 30 minutes with no client and no agent work). Does nothing when none is running. A controller that is working on
                   a run refuses unless --force is given; what a forced stop interrupts is durable and is resumed by the next controller.
          send     Sends the one typed request given as JSON on standard input ({"operation": ...}) and prints the reply. The operations are start_task, answer_clarification, approve,
                   request_changes, accept, abort and continue_task, besides the read-only ones; each names the task and gate it decides and the exact state it was shown (see docs/architecture.md).
                   A malformed or unknown request is refused here, before anything is sent.
          serve    Run the controller in the foreground, for development and debugging. Ctrl+C stops it.

        The controller listens only on a local endpoint private to your operating-system user (a named pipe on Windows, a Unix-domain socket on Linux and macOS); it opens no
        network port. The controller owns the runs it works on: it holds each run's lock while an agent works, so no other process can drive the same run (the lock is released by the
        operating system if the controller dies, and the next controller resumes what can be resumed safely). 'aw status', 'aw runs' and every other read keep working directly. Failures are JSON ({"schemaVersion": 1, "error": {"code": ..., "message": ...}}, as for 'aw status --json') with exit 2 usage, 3 not found, 4 state unreadable, 31 controller unavailable, 32
        incompatible protocol.
        """;

    private const string Runs = """
        Usage: aw runs [--limit <n>]

        Lists the runs kept in the state root, newest first (the 10 most recent unless --limit says otherwise, at most 1000), so a lost run id can be found
        again. Each run shows its id, workflow state (replayed from its durable event stream, as 'aw status' does), when it started, its acceptance
        requirement, its workspace and the first line of its task. Pass an id to 'aw status' for the full picture. A run whose state cannot be read is listed as
        UNREADABLE and left untouched. Starts no agent, makes no model call, writes nothing; works from any later process.

        """;

    private const string Accept = """
        Usage: aw accept <run-id> [--gate <gate-id>]

        ACCEPTS the verified result at the run's open gate and completes the run. Only runs with acceptance human have a gate; for a run with acceptance none this is
        refused (exit 18), because that run completes by itself and no gate exists. Works from any process, after the run's own process and agent session
        ended; it makes no model call. The decision is the command: nothing else on the line is read as approval.
        It is refused (exit 18, nothing recorded) if there is no open gate, the gate is unknown to the run or already decided, it is stale (the run moved
        past what it describes), or the working tree is no longer exactly the state that was verified. --gate pins the decision to the gate you reviewed.

        """;

    private const string Approve = """
        Usage: aw approve <run-id> --gate <gate-id> [--interactive | --non-interactive]

        APPROVES the WHAT (exit 20) or HOW (exit 22) proposal the agent made ('aw status' prints the proposal and its id); the named gate decides which. A HOW approval
        records a human_how_approval semantic revision and resumes the same conversation with the approved HOW; it does not implement or complete anything by itself. The gate id is required: an approval is
        always about a named proposal, so a stale or wrong one cannot be applied by accident, and nothing else on the line is read as approval.
        Records the approval and a new semantic revision (human_what_approval) in one step, returns the run to RUNNING and continues the work in the agent's own
        native conversation, resumed in a new process and sent the approved WHAT. Approving implements, verifies and completes nothing by itself: the agent
        implements afterwards and the controller verifies it (the run completes with acceptance none, or its ACCEPT gate opens with acceptance human).
        Refused (exit 18, nothing recorded) when the gate is unknown to the run, already decided, stale, not a WHAT or HOW proposal, or not the run's open gate.
        If the runtime fails after your approval was recorded, the run is BLOCKED and 'aw continue' retries the same work. To send the proposal back instead use
        'aw request-changes'; to stop, 'aw abort'. The project's configuration must be unchanged since the run started.

        """;

    private const string RequestChanges = """
        Usage: aw request-changes <run-id> --feedback "<what to change>" [--gate <gate-id>] [--interactive | --non-interactive]

        Does NOT accept. At a WHAT proposal it sends the proposal back: the gate is closed (CHANGES_REQUESTED), the feedback becomes a new semantic revision, and the
        same conversation is told to revise the WHAT and not to implement yet; the run ends with exit 20 again once the revised proposal is recorded. A HOW proposal works the
        same way (a human_how_changes revision; exit 22 again once the revised HOW is recorded).
        Closes the open gate as not approved (it can never be accepted afterwards), returns the run to RUNNING under a new semantic
        revision, and continues the work in the agent's own native conversation: it is resumed in a new process and sent only your feedback. Only when it cannot
        be resumed (an explicit, reported fallback) is a fresh session started, which is sent the original task as well. The controller then verifies again and a
        new ACCEPT gate opens (exit 17). Feedback is bounded (2000 characters). The project's configuration must be unchanged since the run started.
        If the runtime fails after your decision was recorded, the run is BLOCKED (exit 14, or 11 / 130) and 'aw continue' retries the same work.

        """;

    private const string Answer = """
        Usage: aw answer <run-id> --question <question-id> --text "<your answer>" [--interactive | --non-interactive]

        Answers a question the agent raised because something the task depends on was missing or ambiguous (the run ended with exit 19; 'aw status' prints the
        question, why it matters and its id). The question id is required: an answer is always about a named question, so a stale or wrong one cannot be applied
        by accident. Free text is valid here because it is an answer; it is never read as an approval, and nothing is accepted by answering.
        Records the answer and a new semantic revision (human_clarification) in one step, returns the run to RUNNING and continues the work in the agent's own
        native conversation, resumed in a new process and sent only the question and your answer. Only when it cannot be resumed (an explicit, reported
        fallback) is a fresh session started, which is sent the task and everything people added so far, in order; if that does not fit its bound it is refused.
        Then the controller verifies: the run completes (acceptance none, exit 0) or its ACCEPT gate opens (acceptance human, exit 17).
        Refused (exit 18, nothing recorded) when the question is unknown to the run, already answered, stale, or is not the run's open question; the answer
        is bounded (2000 characters). If the runtime fails after your answer was recorded, the run is BLOCKED and 'aw continue' retries the same work.
        Cancel the run instead with 'aw abort'. The project's configuration must be unchanged since the run started.

        """;

    private const string Continue = """
        Usage: aw continue <run-id> [--interactive | --non-interactive]

        Retries the work you already asked for with 'aw request-changes' (or the answer you gave with 'aw answer', or the approval you gave with 'aw approve') when its continuation could not run because the runtime failed (the run is BLOCKED and
        'aw status' says so). It is an operational retry, not a decision: it records no new gate decision, no new semantic revision and no new gate, and it takes
        no feedback (the recorded feedback is the requirement). The same native conversation is resumed when it can be (same rules as request-changes); the agent
        is told to inspect the workspace and keep correct work, because the interrupted attempt may already have changed it. The controller verifies whatever
        state results and opens a new ACCEPT gate (exit 17). A failure again leaves the run BLOCKED and retryable.
        Only that one state is recoverable: a run that is RUNNING, AWAITING_HUMAN, COMPLETED or CANCELLED, or BLOCKED for another reason (for example a native
        interaction nobody answered), is refused (exit 18, nothing recorded). The configuration must be unchanged since the run started. Cancel instead with 'aw abort'.

        """;

    private const string Bind = """
        Usage: aw bind <run-id> --role <root|reviewer|prover> --profile <profile-id>

        Binds a role the run needs and has no binding for to one of the project's profiles (see 'aw show'), and records the execution configuration the profile resolves to now
        (its harness, model and reasoning). 'aw status' lists the roles a run is missing, for example a reviewer after an agent asked for an independent review. A role is never given a
        profile that was not named here, and a binding is never replaced: a role already bound to another profile is refused (exit 18); one already bound to this profile is left as it is.
        It starts nothing: continue the run afterwards with 'aw continue'. It is refused while something is working on the run, and for a profile the harness cannot run or cannot honour (exit 2).

        """;

    private const string Abort = """
        Usage: aw abort <run-id>

        Cancels a BLOCKED run (the usual result of exit 15: its agent session ended; or one whose continuation failed and that 'aw continue' could retry), one left AWAITING_HUMAN by a dead process, or a run waiting at the
        ACCEPT gate (the gate is closed without acceptance). The workflow becomes CANCELLED. It answers nothing the agent asked. A run that is still live
        in another process is answered in the agent's own session there (or stopped with Ctrl+C), not here.

        """;

    private const string Show = """
        Usage: aw show [--workspace <dir>] [--config <file>]

        Validates the project configuration and prerequisites and prints what a run would use. Starts no agent and makes no model call.

        """;

    private const string Project = """
        Usage: aw project inspect [--workspace <dir>] [--config <file>]
               aw project init --workspace <dir> --request-file <absolute path>

        Typed project operations for an integration that must not read or write project files itself. Both print one JSON document.
        inspect  Whether the workspace is configured ('missing', 'invalid' with the problems, or 'ready'), its profiles (harness, model, reasoning, and whether the installed harness can honour each),
                 and, from the files at its root, what could be configured (a suggestion is evidence to confirm, never a default). Read-only.
        init     Creates agenticworkflow.json in the workspace from the choices in the request file (read once, then deleted): toolchain, profiles, the implementer profile and optional reviewer and
                 prover profiles, verification checks, writable and protected paths, acceptance. Nothing is defaulted or invented; the file is validated by the same strict parser 'aw run' uses and
                 an existing configuration is never replaced.

        """;

    private const string Doctor = """
        Usage: aw doctor [--workspace <dir>] [--json]

        Diagnoses this installation and machine and prints PASS, WARN or FAIL for each check: the product version and that the installed files are whole, the evidence hook and the Pi extension,
        whether the state folder is writable, the controller (it is started if none is running, as any command would, and reported with its version and number of active runs), Git, Herdr's
        'hc', Claude Code and Pi (each optional, at least one needed), the Herdr cockpit (a copy, with its command-palette entry, pointing at this aw, at this version) and, when run inside a
        project (or with --workspace), whether the project's configuration is valid and its profiles can run here.
        It starts no agent and makes no model call, and prints no secret or conversation reference. Exit 0 when nothing failed (warnings are optional things that are missing); 33 when something did.
        --json prints the same result as one JSON document.

        """;

    private const string Attach = """
        Usage: aw attach <run-id> [--describe]

        Attaches this terminal to the agent's own Herdr session for the run, where the agent's permission, trust and login questions are answered by a person (aw never answers them). With
        --describe it prints one JSON document saying whether such a session exists and what the harness reports it is doing, and attaches nothing. Only a run that a process is working on has one.

        """;
}
