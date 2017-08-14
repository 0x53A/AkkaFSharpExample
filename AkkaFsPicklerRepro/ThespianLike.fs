
namespace Rokku.Internal

open System

open Akka.Actor

// marker interface: all top-level messages need to be marked with this
// reason: using a different serializer
type ISerialMessage = interface end


// all replys are wrapped in this
type ReplyChannelResult<'T, 'TError> = 
| Ok of 'T
| Error of exn
    interface ISerialMessage


type IActorRef<'T> =
    abstract member Tell : 'T -> unit
    abstract member Raw : IActorRef

type IReplyChannel<'T> =
    abstract member ReplyWithValue : 'T -> unit
    abstract member ReplyWithException : exn -> unit


namespace Rokku.Internal.Thespian

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

open FSharp.Control.Tasks.ContextInsensitive

open Akka
open Akka.Actor

open Rokku
open Rokku.Internal

type CallbackActor<'T>(cb:'T->unit) =
    inherit ReceiveActor()
    do base.Receive<'T>(cb)
    static member Props(cb:'T->unit) =
        Props.Create<CallbackActor<'T>>(cb)
 

type ActorRef<'T>(ref:IActorRef) =
    interface IActorRef<'T> with
        member self.Tell msg = ref.Tell msg
        member self.Raw = ref

type ReplyChannel<'T>(ref:IActorRef<ReplyChannelResult<'T, exn>>) =
    interface IReplyChannel<'T> with
        member self.ReplyWithValue(v) =
            ref.Tell(ReplyChannelResult.Ok v)
        member self.ReplyWithException ex =
            ref.Tell(ReplyChannelResult.Error ex)
        
// this is a more or less direct port of Akka.Net Ask (see Futures.cs)
module internal AkkaInternalHelpers =
    open Akka.Actor.Internal

    let rec ResolveProvider(self:ICanTell) =
        if InternalCurrentActorCellKeeper.Current <> null then
            InternalCurrentActorCellKeeper.Current.SystemImpl.Provider
        else
            match self with
            | :? IInternalActorRef as iar -> iar.Provider
            | :? ActorSelection as actS -> ResolveProvider(actS.Anchor)
            | _ -> null

    let Ask(self:ICanTell, messageCb:IActorRef->obj, provider:IActorRefProvider, timeoutOpt:TimeSpan option, cancellationToken:CancellationToken) =
        let result = TaskCompletionSource<obj>(TaskCreationOptions.RunContinuationsAsynchronously)

        let mutable timeoutCancellation = null
        let timeout = defaultArg timeoutOpt provider.Settings.AskTimeout
        let ctrList = List<CancellationTokenRegistration>(2)

        if timeout <> Timeout.InfiniteTimeSpan && timeout > TimeSpan.Zero then
            timeoutCancellation <- new CancellationTokenSource()
            ctrList.Add(timeoutCancellation.Token.Register(fun () -> result.TrySetCanceled() |> ignore))
            timeoutCancellation.CancelAfter(timeout)
            
        if cancellationToken.CanBeCanceled then
            ctrList.Add(cancellationToken.Register(fun () -> result.TrySetCanceled() |> ignore))
        
        //create a new tempcontainer path
        let path = provider.TempPath()
        //callback to unregister from tempcontainer
        let unregister () =
            // cancelling timeout (if any) in order to prevent memory leaks
            // (a reference to 'result' variable in CancellationToken's callback)
            if timeoutCancellation <> null then            
                timeoutCancellation.Cancel()
                timeoutCancellation.Dispose()
            
            for i in 0 .. ctrList.Count-1 do
                ctrList.[i].Dispose()
            
            provider.UnregisterTempActor(path)

        let future = new FutureActorRef(result, Action(unregister), path)
        //The future actor needs to be registered in the temp container
        provider.RegisterTempActor(future, path)
        let message = messageCb(future)
        self.Tell(message, future)
        result.Task

// put the extension members in the global namespace
namespace global

open System
open System.Threading
open System.Threading.Tasks
open FSharp.Control.Tasks.ContextInsensitive

open Akka.Actor

open Rokku.Internal
open Rokku.Internal.Thespian

[<AutoOpen>]
module ThespianLike =
    open Rokku.Internal.ExceptionReraiseHelper

    let asTyped<'T> (actor:IActorRef) : IActorRef<_> = upcast ActorRef<'T>(actor)

    type Akka.Actor.IActorRef with
        member self.AsTyped<'T>() : IActorRef<_> = upcast ActorRef<'T>(self)

    type IActorRef<'T> with
        member self.Ask<'TResult> (cb:IReplyChannel<'TResult> -> 'T, ?timeout, ?cancelToken) : Task<'TResult> = task {
            let provider = AkkaInternalHelpers.ResolveProvider(self.Raw)
            let messageCb (aRef:IActorRef) : obj =
                let typedARef = ActorRef<ReplyChannelResult<'TResult, exn>>(aRef)
                let channel = ReplyChannel<'TResult>(typedARef)
                let msg : 'T = cb(channel)
                upcast msg
            let ct = defaultArg cancelToken CancellationToken.None
            let! result = AkkaInternalHelpers.Ask(self.Raw, messageCb, provider, timeout, ct)
            let value = 
                match result with
                | null -> failwithf "Internal Error in Ask: expected object of type %s but received null" typeof<ReplyChannelResult<'TResult, exn>>.Name
                | :? ReplyChannelResult<'TResult, exn> as r ->
                    match r with
                    | ReplyChannelResult.Ok v -> v
                    | ReplyChannelResult.Error ex -> reraise' ex
                | _ -> failwithf "Internal Error in Ask: expected object of type %s but got a %s" typeof<ReplyChannelResult<'TResult, exn>>.Name (result.GetType().Name)
            return value                    
        }

        member self.AskSync (cb:IReplyChannel<'TResult> -> 'T, ?timeout, ?cancelToken) : 'TResult =
            try
                let t =  self.Ask(cb, ?timeout=timeout, ?cancelToken=cancelToken)
                t.Result
            with
              :? AggregateException as aex ->
                let innerEx =
                    let flattened = aex.Flatten()
                    if flattened.InnerExceptions.Count = 1 then
                        reraise' flattened.InnerException
                    else
                        reraise' flattened
                raise(exn("Ask failed", innerEx))


    type IReplyChannel<'T> with

        member self.Run cb =
            try
                let v = cb()
                self.ReplyWithValue(v)
            with
              exn ->
                self.ReplyWithException(exn) 

        member self.RunLog (doLog:obj->unit) cb =
            try
                let v = cb()
                doLog v
                self.ReplyWithValue(v)
            with
              exn ->
                doLog exn
                self.ReplyWithException(exn) 

    type ActorSystem with
        member x.FromCallback(cb:Action<'T>) : IActorRef<'T> =
            x.ActorOf(CallbackActor.Props(fun msg -> cb.Invoke(msg))) |> asTyped
