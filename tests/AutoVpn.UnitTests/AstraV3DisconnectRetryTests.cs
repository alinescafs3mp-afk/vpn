using AutoVpn.Application;
using AutoVpn.Contracts;
using AutoVpn.Domain;

namespace AutoVpn.UnitTests;

public sealed class AstraV3DisconnectRetryTests
{
    [Theory]
    [InlineData(1,true)]
    [InlineData(2,true)]
    [InlineData(3,false)]
    [InlineData(0,false)]
    public void SideEffectFreeStaleRejectionHasStrictAttemptBound(int attempts, bool expected)
        => Assert.Equal(expected, DisconnectRetry.ShouldRetry(Request(), Reply(), true, attempts));

    [Theory]
    [InlineData("request-id")]
    [InlineData("protocol")]
    [InlineData("success")]
    [InlineData("connect")]
    [InlineData("cleanup")]
    [InlineData("busy")]
    [InlineData("no-snapshot")]
    [InlineData("old-revision")]
    [InlineData("revision-mismatch")]
    [InlineData("sequence-mismatch")]
    [InlineData("mailbox-refused")]
    public void UncertainOrUnacceptedResponseNeverTriggersAnAutomaticEffect(string kind)
    {
        var request=Request();var response=Reply();var accepted=true;
        switch(kind)
        {
            case "request-id": response=response with {RequestId="another"};break;
            case "protocol": response=response with {ProtocolVersion=99};break;
            case "success": response=response with {Ok=true};break;
            case "connect": request=request with {Operation=IpcOperations.Connect};break;
            case "cleanup": response=response with {ErrorCode="CLEANUP_UNCERTAIN"};break;
            case "busy": response=response with {ErrorCode="BUSY"};break;
            case "no-snapshot": response=response with {Snapshot=null};break;
            case "old-revision": request=request with {ExpectedStateRevision=2};break;
            case "revision-mismatch": response=response with {StateRevision=3};break;
            case "sequence-mismatch": response=response with {StateSequence=3};break;
            case "mailbox-refused": accepted=false;break;
        }
        Assert.False(DisconnectRetry.ShouldRetry(request,response,accepted,1));
    }
    private static IpcRequest Request()=>new(){ProtocolVersion=ProductLimits.IpcProtocolVersion,RequestId="one",Operation=IpcOperations.Disconnect,ExpectedStateRevision=1};
    private static IpcResponse Reply()=>new(){ProtocolVersion=ProductLimits.IpcProtocolVersion,RequestId="one",Ok=false,ErrorCode=ReasonCodes.StaleRevision,StateRevision=2,StateSequence=2,Snapshot=new(){Revision=2,Sequence=2}};
}
