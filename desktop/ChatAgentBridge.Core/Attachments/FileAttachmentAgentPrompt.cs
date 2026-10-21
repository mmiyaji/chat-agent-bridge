namespace ChatAgentBridge.Core;

public static class FileAttachmentAgentPrompt
{
    private const string CapabilityNote = "Attachment delivery capability note: You are running through Chat Agent Bridge. When the user requests a chat attachment, create the requested deliverable as a new output file in the current workspace, using a unique filename for discovery. This concerns shared output files, not ordinary edits required by the task. After successful execution, the bridge discovers eligible files newly created during this run and handles Slack/Mattermost uploads under its separate file consent and size/count limits. This instruction does not grant consent or change those policies. The command-line agent does not need chat upload tools. Do not claim a file was sent unless the bridge confirms it. Do not use alternate upload APIs or copy workspace files wholesale to trigger discovery. Workspace access, tool approval, and network permissions are unchanged.";

    public static string Build(string userPrompt, BridgeSettings settings, bool deliveryAvailable)
    {
        ArgumentNullException.ThrowIfNull(userPrompt);
        ArgumentNullException.ThrowIfNull(settings);

        if (!deliveryAvailable
            || FileAttachmentPolicy.NormalizeMode(settings.FileAttachmentApprovalMode) == "disabled"
            || ExecutionPolicy.NormalizeChatResultMode(settings.ChatResultMode) == "none")
        {
            return userPrompt;
        }

        return $"{CapabilityNote}\n\n--- Original user prompt (preserved verbatim) ---\n{userPrompt}\n--- End original user prompt ---";
    }
}
