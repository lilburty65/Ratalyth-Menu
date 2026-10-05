using System;

namespace Ratalyth.Managers.DiscordRPC.Converters
{
    internal class EnumValueAttribute : Attribute
    {
        public string Value { get; set; }
        public EnumValueAttribute(string value)
        {
            Value = value;
        }
    }
}
