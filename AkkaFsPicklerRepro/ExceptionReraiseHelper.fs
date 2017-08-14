namespace Rokku.Internal

open System
open System.Reflection

// see http://fssnip.net/k1
module internal ExceptionReraiseHelper =

    let clone (e : #exn) =
        let bf = new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter()
        use m = new System.IO.MemoryStream()
        bf.Serialize(m, e)
        m.Position <- 0L
        bf.Deserialize m :?> exn

    let remoteStackTraceField =
        let getField name = typeof<System.Exception>.GetField(name, BindingFlags.Instance ||| BindingFlags.NonPublic)
        match getField "remote_stack_trace" with
        | null ->
            match getField "_remoteStackTraceString" with
            | null -> failwith "a piece of unreliable code has just failed."
            | f -> f
        | f -> f

    let inline reraise' (e : #exn) =
        // clone the exception to avoid mutation side-effects
        let e' = clone e
        remoteStackTraceField.SetValue(e', e'.StackTrace + System.Environment.NewLine)
        raise e'



