using System;
using LiteNetLib;
using MessagePack;

namespace GameCult.Networking
{

    [Union(0, typeof(LoginMessage)),
     Union(1, typeof(RegisterMessage)),
     Union(2, typeof(VerifyMessage)),
     Union(3, typeof(LoginSuccessMessage)),
     Union(4, typeof(ErrorMessage)),
     Union(5, typeof(ChangeNameMessage)),
     MessagePackObject]
    public abstract class Message
    {
        [IgnoreMember] public NetPeer Peer { get; set; }
    }

    public class EncryptedMessage : Message
    {
        [Key(0)] public byte[] Nonce;
    }

    [MessagePackObject]
    public class LoginMessage : EncryptedMessage
    {
        [Key(1)] public byte[] Auth;
        [Key(2)] public byte[] Password;
    }

    [MessagePackObject]
    public class RegisterMessage : EncryptedMessage
    {
        [Key(1)] public byte[] Email;
        [Key(2)] public byte[] Password;
        [Key(3)] public byte[] Name;
    }

    [MessagePackObject]
    public class VerifyMessage : EncryptedMessage
    {
        [Key(1)] public byte[] Session;
    }

    [MessagePackObject]
    public class LoginSuccessMessage : EncryptedMessage
    {
        [Key(1)] public byte[] Session;
    }

    [MessagePackObject]
    public class ErrorMessage : Message
    {
        [Key(0)] public string Error;
    }

    [MessagePackObject]
    public class ChangeNameMessage : Message
    {
        [Key(0)] public string Name;
    }

    [MessagePackObject]
    public class ChatMessage : Message
    {
        [Key(0)] public string Text;
    }
}