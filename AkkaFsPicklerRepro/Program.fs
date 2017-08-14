namespace Rokku

open System
open System.Diagnostics

open Akka
open Akka.Actor
open Akka.Configuration

open Rokku.Internal
open Rokku.Internal.Thespian
open Rokku.Actor



type GreetingActor() as this =
    inherit ReceiveActor()

    do base.Receive<GreetingActorMessage>(this.OnReceiveGreetingActorMessage)

    member self.OnReceiveGreetingActorMessage(msg:GreetingActorMessage):unit =
        match msg with 
        | GreetMeBack (msg, rc) -> rc.ReplyWithValue(msg)

    static member Props() =
        Props.Create<GreetingActor>()


type SystemConfiguration =
| LocalInProcess of name:string
| Network of name:string * hostname:string * port:uint32 option

module RokkuActorSystem =
    open FSharp.Control.Tasks.ContextInsensitive

    let private configString (hostname:string, port:uint32 option) =
        let configStr = sprintf """
akka {
    actor {
        provider = "Akka.Remote.RemoteActorRefProvider, Akka.Remote"
        ask-timeout = %is
    }
    suppress-json-serializer-warning = true
    remote {
        helios.tcp {
            port = %i
            hostname = "%s"
            # Maximum frame size: 4 MB
            maximum-frame-size = 4000000b
        }
    }
}
"""                       (int FixedConfiguration.ActorCommunicationTimeout.TotalSeconds) (defaultArg port 0u) hostname
        configStr

    let private setupSystem(config:SystemConfiguration) =
        match config with
        | LocalInProcess name ->
            let configStr = sprintf "akka {
    actor { ask-timeout = %is }
    suppress-json-serializer-warning = true
}"                           (int FixedConfiguration.ActorCommunicationTimeout.TotalSeconds)
            let config = ConfigurationFactory.ParseString( configStr )
            let system = ActorSystem.Create (name, config)
            system
        | Network (name, hostname, port) ->
            let config = ConfigurationFactory.ParseString( configString(hostname, port) )
            let actorSystem = Akka.Actor.ActorSystem.Create(name, config)
            // create serializer and associate it with ISerialMessage
            let fsharpSerializer = Serializer.FsSerializer(actorSystem :?> Akka.Actor.ExtendedActorSystem)
            actorSystem.Serialization.AddSerializer(fsharpSerializer)
            actorSystem.Serialization.AddSerializationMap(typeof<ISerialMessage>, fsharpSerializer)
            actorSystem

    let spawnActors (actorSystem:ActorSystem) =
        let greeter =
            actorSystem.ActorOf<GreetingActor>(typeof<GreetingActor>.Name)
            |> asTyped<GreetingActorMessage>
        let registry =
            actorSystem.ActorOf(RegistryActor.Props(greeter), typeof<RegistryActor>.Name)
            |> asTyped<RegistryActorMessage>
        registry

    let setupNetworkSystem port =   

        let hostName = System.Net.Dns.GetHostEntry(Environment.MachineName).HostName

        // a system name may only contain alphanumeric and non-leading dashes
        let sanitizedHostName =
            let isAlphaNumeric c =
                ('0' <= c && c <= '9') ||
                ('a' <= c && c <= 'z') ||
                ('A' <= c && c <= 'Z')
            let filtered =
                hostName.Replace(".", "-")
                |> Seq.filter (fun c -> c = '-' || isAlphaNumeric c)
                |> Seq.toArray
                |> String
            filtered.Trim('-')
        // use the sanitized hostname and append the current process id (so that there is no collision with multiple instances)
        let systemName = sprintf "%s-%i" sanitizedHostName (Process.GetCurrentProcess().Id)
        let actorSystem = setupSystem(Network(systemName, hostName, port))
        actorSystem

    let setupNetworkSystemAndActors (port:uint32 option) =
        let actorSystem = setupNetworkSystem port
        let extSystem = actorSystem :?> Akka.Actor.ExtendedActorSystem
        let actualAddress = extSystem.Provider.DefaultAddress
        let registry = spawnActors actorSystem
        let thisActorRemotePath = sprintf "akka.tcp://%s@%s:%i/user/%s" actorSystem.Name actualAddress.Host actualAddress.Port.Value registry.Raw.Path.Name
        actorSystem, registry, thisActorRemotePath

    let ConnectToExistingSystem (actorUri:string) = task {
        let system = setupNetworkSystem(None)
        let registryActorSelect = system.ActorSelection(actorUri)
        let! registryActorRefUntyped = registryActorSelect.ResolveOne(FixedConfiguration.ActorCommunicationTimeout)
        let registryActorRef = registryActorRefUntyped |> asTyped
        let! greeter = registryActorRef.Ask(RegistryActorMessage.GetGreetingActor)
        return greeter
    }



module Program =
    open DotNetty.Codecs
    open FSharp.Control.Tasks.ContextInsensitive


    [<EntryPoint>]
    let main argv =
    
        match argv with
        | [| "server" |] ->
            let system, registry, path = RokkuActorSystem.setupNetworkSystemAndActors None
            printfn "Spawned server at %s" path
            System.Threading.Thread.Sleep(-1)
        | [| "client" ; serverUrl |] ->
            let loopTask = task {
                let! greeter = RokkuActorSystem.ConnectToExistingSystem(serverUrl)
                while true do
                    printfn "Input greeting:"
                    let line = Console.ReadLine()
                    let! response = greeter.Ask(fun rc -> GreetingActorMessage.GreetMeBack(line, rc))
                    printfn "Yay, I got a response: %s" response
            }
            System.Threading.Thread.Sleep(-1)
        | _ -> eprintfn "Error, invalid command line args: %A" argv
        0 // return an integer exit code
