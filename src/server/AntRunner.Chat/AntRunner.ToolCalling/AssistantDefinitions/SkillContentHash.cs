using System.Security.Cryptography;

namespace AntRunner.ToolCalling.AssistantDefinitions;

public static class SkillContentHash
{
    public static string Compute(byte[] contentBytes)
    {
        var hash = SHA256.HashData(contentBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
