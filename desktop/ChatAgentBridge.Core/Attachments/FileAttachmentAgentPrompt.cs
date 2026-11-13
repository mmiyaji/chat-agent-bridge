namespace ChatAgentBridge.Core;

public static class FileAttachmentAgentPrompt
{
    private const string CapabilityNote = "Attachment delivery capability note: You are running through Chat Agent Bridge. When the user requests a chat attachment, create the requested deliverable as a new output file in the current workspace, using a unique filename for discovery. Only files newly created during this run are discovered, so if the user asks to attach a file that already exists (for example an image made in an earlier turn or saved outside the workspace), copy just that file to a new uniquely named file in the workspace. This concerns shared output files, not ordinary edits required by the task. After successful execution, the bridge discovers eligible files newly created during this run and handles Slack/Mattermost/Discord uploads under its separate file consent and size/count limits. This instruction does not grant consent or change those policies. The command-line agent does not need chat upload tools. Do not claim a file was sent unless the bridge confirms it. Do not use alternate upload APIs or copy workspace files wholesale or copy files the user did not ask to attach. Workspace access, tool approval, and network permissions are unchanged.";

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

        var scopeNote = FileAttachmentPolicy.NormalizeScope(settings.FileAttachmentScope) switch
        {
            "conversation" => " To attach a file that was already created earlier in this conversation, do not copy it; instead add a line \"CAB-ATTACH: <workspace-relative path>\" to your final reply.",
            "workspace" => " To attach an existing workspace file that the user explicitly asked for, do not copy it; instead add a line \"CAB-ATTACH: <workspace-relative path>\" to your final reply. Name only files the user asked for; the user approves every such file on the PC.",
            _ => ""
        };
        return $"{CapabilityNote}{scopeNote}\n\n--- Original user prompt (preserved verbatim) ---\n{userPrompt}\n--- End original user prompt ---";
    }
}
