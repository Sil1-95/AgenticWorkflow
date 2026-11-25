using AgenticWorkflow.Application.Controller;
using AgenticWorkflow.Core.Identity;

namespace AgenticWorkflow.Application.Tests;

/// <summary>
/// The typed shape of the role-binding requests: <c>start_task.bindings</c> and <c>bind_role</c> carry data only (a role id and a profile id, both closed in form), are validated before
/// anything is recorded, and a role or profile that is not one of those is refused with a structured error rather than adapted.
/// </summary>
public sealed class BindingRequestValidationTests
{
    private static StartTaskRequest Start(IReadOnlyDictionary<string, string>? bindings)
        => new() { Objective = "Do the thing.", WorkingDirectory = OperatingSystem.IsWindows() ? @"C:\work" : "/work", Bindings = bindings };

    [Fact]
    public void A_start_with_no_bindings_carries_none_and_chooses_none()
    {
        var (instruction, error) = WorkflowRequestValidator.Validate(Start(null));

        Assert.Null(error);
        Assert.Empty(Assert.IsType<StartTaskInstruction>(instruction).Bindings);
    }

    [Fact]
    public void A_start_with_explicit_bindings_carries_exactly_those()
    {
        var (instruction, error) = WorkflowRequestValidator.Validate(Start(new Dictionary<string, string> { ["root"] = "claude-main", ["reviewer"] = "claude-review" }));

        Assert.Null(error);
        var bindings = Assert.IsType<StartTaskInstruction>(instruction).Bindings;
        Assert.Equal(2, bindings.Count);
        Assert.Equal(new ProfileId("claude-main"), bindings[new RoleId("root")]);
        Assert.Equal(new ProfileId("claude-review"), bindings[new RoleId("reviewer")]);
    }

    [Theory]
    [InlineData("implementer", "claude-main")]      // not a bindable role
    [InlineData("root", "Claude Main")]             // not a profile id
    [InlineData("root", "a;b")]
    [InlineData("root", "")]
    public void A_start_naming_a_role_or_profile_that_is_not_one_is_refused_before_anything_starts(string role, string profile)
    {
        var (instruction, error) = WorkflowRequestValidator.Validate(Start(new Dictionary<string, string> { [role] = profile }));

        Assert.Null(instruction);
        Assert.Equal(ControllerErrorCodes.InvalidArgument, error!.Code);
    }

    [Fact]
    public void A_start_naming_more_roles_than_exist_is_refused()
    {
        var many = Enumerable.Range(0, 9).ToDictionary(i => $"role{i}", _ => "claude-main");

        Assert.Equal(ControllerErrorCodes.InvalidArgument, WorkflowRequestValidator.Validate(Start(many)).Error!.Code);
    }

    [Fact]
    public void A_bind_request_is_validated_and_carries_typed_ids_only()
    {
        var task = Guid.NewGuid().ToString("D");

        var (instruction, error) = WorkflowRequestValidator.Validate(new BindRoleRequest { TaskId = task, Role = "reviewer", Profile = "claude-review" });

        Assert.Null(error);
        var bind = Assert.IsType<BindRoleInstruction>(instruction);
        Assert.Equal((new RoleId("reviewer"), new ProfileId("claude-review")), (bind.Role, bind.Profile));
        Assert.Equal(task, bind.TaskId.ToString());
    }

    [Theory]
    [InlineData("not-a-guid", "reviewer", "claude-review", ControllerErrorCodes.InvalidTaskId)]
    [InlineData("guid", "implementer", "claude-review", ControllerErrorCodes.InvalidArgument)]
    [InlineData("guid", "reviewer", "Claude Review", ControllerErrorCodes.InvalidArgument)]
    [InlineData("guid", "reviewer", "", ControllerErrorCodes.InvalidArgument)]
    public void A_malformed_bind_request_is_refused_with_a_structured_error(string task, string role, string profile, string code)
    {
        var request = new BindRoleRequest { TaskId = task == "guid" ? Guid.NewGuid().ToString("D") : task, Role = role, Profile = profile };

        var (instruction, error) = WorkflowRequestValidator.Validate(request);

        Assert.Null(instruction);
        Assert.Equal(code, error!.Code);
    }

    [Fact]
    public void Bind_role_is_a_workflow_request_and_a_documented_operation()
    {
        Assert.True(WorkflowRequestValidator.IsWorkflowRequest(new BindRoleRequest { TaskId = "x", Role = "reviewer", Profile = "p" }));
        Assert.Contains(ControllerOperations.BindRole, ControllerOperations.All);
    }
}
