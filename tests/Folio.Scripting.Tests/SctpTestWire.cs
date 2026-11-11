using System.Buffers.Binary;
using Folio.Scripting.Rtc;

namespace Folio.Scripting.Tests;

internal static class SctpTestWire
{
    internal static void DrainAndAcknowledge(SctpReliableDelivery sender, TimeProvider clock)
    {
        for (var turn = 0; turn < 2048; turn++)
        {
            var emitted = sender.TakeSendable(clock);
            if (emitted.Count == 0 && sender.FlightBytes == 0) return;
            var sack = new byte[12];
            BinaryPrimitives.WriteUInt32BigEndian(sack, sender.HighestEmittedTsn);
            BinaryPrimitives.WriteUInt32BigEndian(sack.AsSpan(4), SctpReliableDelivery.MaxReceiveBytes);
            Assert.True(sender.Acknowledge(sack, clock));
        }
        Assert.Fail("Sender failed to drain with positive receiver credit and acknowledgements.");
    }
}
