// see: https://github.com/akkadotnet/akka.net/issues/999#issuecomment-118635668
namespace Rokku.Internal

open MBrace.FsPickler
open Akka.Serialization
open System


module AkkaNetSurrogatePickler =
    open Akka.Util
    open Akka.Actor

    let mkPickler (resolver : IPicklerResolver) =
        let surrogatePickler = resolver.Resolve<ISurrogate>()

        let writer (w : WriteState) (ns : ISurrogated) =
            let system = w.StreamingContext.Context :?> ActorSystem
            let surrogate = ns.ToSurrogate(system)
            surrogatePickler.Write w "surrogatedValue" surrogate

        let reader (r : ReadState) =
            let system = r.StreamingContext.Context :?> ActorSystem
            let surrogate = surrogatePickler.Read r "surrogatedValue"
            surrogate.FromSurrogate(system)

        Pickler.FromPrimitives(reader, writer, useWithSubtypes=true)



module Serializer =
    open System.Runtime.Serialization
    open Akka.Util
    
    let getStreamingContext o = StreamingContext(StreamingContextStates.All, o)
    
    // used for top level serialization
    type FsSerializer(system) = 
        inherit Serializer(system)
        static do
            FsPickler.DeclareSerializable(fun t -> t.GetInterfaces() |> Seq.contains typeof<ISurrogate>)
            FsPickler.RegisterPicklerFactory<ISurrogated> (AkkaNetSurrogatePickler.mkPickler)
        let fsp = FsPickler.CreateBinarySerializer()
        override __.Identifier = 3003 // this value must be unique (over 100)
        override __.IncludeManifest = false        
        override __.ToBinary(o) =
            use stream = new System.IO.MemoryStream()
            fsp.Serialize(stream, o, streamingContext=getStreamingContext system)            
            stream.ToArray()
        override __.FromBinary(bytes, t) =
            use stream = new System.IO.MemoryStream(bytes)
            let obj = fsp.Deserialize(stream, streamingContext=getStreamingContext system) :> obj
            obj
