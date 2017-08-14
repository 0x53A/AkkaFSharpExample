namespace Rokku.Actor

open System.Linq

open Rokku
open Rokku.Internal

open System
open Rokku.Internal.Thespian

// ------------------------------------------------------------
// Greeter
// ------------------------------------------------------------

type GreetingActorMessage =
| GreetMeBack of string * IReplyChannel<string>
    interface ISerialMessage

// ------------------------------------------------------------
// Registry
// ------------------------------------------------------------

type RegistryActorMessage =
| GetGreetingActor of IReplyChannel<IActorRef<GreetingActorMessage>>
    interface ISerialMessage

