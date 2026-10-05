using Ratalyth.Managers.DiscordRPC.RPC.Payload;

namespace Ratalyth.Managers.DiscordRPC.RPC.Commands
{
    internal interface ICommand
    {
        IPayload PreparePayload(long nonce);
    }
}
