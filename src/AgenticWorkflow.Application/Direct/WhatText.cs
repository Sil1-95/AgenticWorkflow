using System.Text;
using AgenticWorkflow.Core.Gates;

namespace AgenticWorkflow.Application.Direct;

/// <summary>
/// The one rendering of a <see cref="WhatProposal"/> as plain text, shared by everything that tells an agent what was approved or sent back (the resumed turns and
/// the fresh-session history). Nothing is paraphrased or shortened: each present section is written under its own name, exactly as it was recorded.
/// </summary>
internal static class WhatText
{
    public static string Render(WhatProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        var text = new StringBuilder();

        foreach (var (name, section) in proposal.Sections())
        {
            text.AppendLine($"{name}:");
            text.AppendLine(section.Trim());
        }

        return text.ToString().TrimEnd();
    }
}
