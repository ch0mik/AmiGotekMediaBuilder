namespace AmiGotekMediaBuilder.Core.Networking;

public sealed class UnsafeUrlException(string message) : InvalidOperationException(message);
