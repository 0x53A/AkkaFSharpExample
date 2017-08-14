namespace Rokku.Actor

open Akka
open Akka.Actor

open Rokku
open Rokku.Internal
open System.Linq.Expressions


// the sole purpose of this actor is to tell people where other actors are.
// (a simple Service Discovery)

type RegistryActor(greeter:IActorRef<GreetingActorMessage>) as this =
    inherit ReceiveActor()

    do base.Receive<RegistryActorMessage>(this.OnReceiveRegistryActorMessage)

    member self.OnReceiveRegistryActorMessage(msg:RegistryActorMessage):unit =
        match msg with 
        | GetGreetingActor c -> c.ReplyWithValue (greeter)

    static member Props(greeter:IActorRef<GreetingActorMessage>) =
        Props.Create<RegistryActor>(greeter)