using FluentAssertions;
using ScriptExecutionAgent;

namespace ScriptExecutionAgent.Tests;

[TestClass]
public sealed class AdminApplyTargetsTests
{
    [TestMethod]
    public void Resolve_global_defaults_to_apt_only()
    {
        var targets = AdminApplyTargets.Resolve(hasScope: false, requestedTargets: null);

        targets.Apt.Should().BeTrue();
        targets.Pip.Should().BeFalse();
        targets.InstallScripts.Should().BeFalse();
    }

    [TestMethod]
    public void Resolve_scoped_defaults_to_pip_and_install_scripts()
    {
        var targets = AdminApplyTargets.Resolve(hasScope: true, requestedTargets: null);

        targets.Apt.Should().BeFalse();
        targets.Pip.Should().BeTrue();
        targets.InstallScripts.Should().BeTrue();
    }

    [TestMethod]
    public void Resolve_global_explicit_apt_is_allowed()
    {
        var targets = AdminApplyTargets.Resolve(hasScope: false, requestedTargets: new[] { "apt" });

        targets.Apt.Should().BeTrue();
    }

    [TestMethod]
    public void Resolve_scoped_explicit_pip_only_is_allowed()
    {
        var targets = AdminApplyTargets.Resolve(hasScope: true, requestedTargets: new[] { "pip" });

        targets.Pip.Should().BeTrue();
        targets.InstallScripts.Should().BeFalse();
    }

    [TestMethod]
    public void Resolve_rejects_global_pip()
    {
        var act = () => AdminApplyTargets.Resolve(hasScope: false, requestedTargets: new[] { "pip" });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Global apply supports only apt*");
    }

    [TestMethod]
    public void Resolve_rejects_scoped_apt()
    {
        var act = () => AdminApplyTargets.Resolve(hasScope: true, requestedTargets: new[] { "apt" });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Scoped apply cannot target apt*");
    }

    [TestMethod]
    public void Resolve_rejects_unknown_target()
    {
        var act = () => AdminApplyTargets.Resolve(hasScope: false, requestedTargets: new[] { "npm" });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not supported*");
    }

    [TestMethod]
    public void Resolve_rejects_scoped_empty_target_set()
    {
        var act = () => AdminApplyTargets.Resolve(hasScope: true, requestedTargets: Array.Empty<string>());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*cannot be empty*");
    }
}
