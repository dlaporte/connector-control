using System.Security.Cryptography;

namespace ConnectorControl.Core;

/// <summary>Content identity for a collection document, used to tell "the file changed" from "the file was rewritten with the same bytes".</summary>
public static class ContentHash
{
    public static string Sha256(byte[] data) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(data));
}
