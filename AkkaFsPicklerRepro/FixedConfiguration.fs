namespace Rokku

open System

module FixedConfiguration =
    open System.Diagnostics

    let private isDebugConfiguration =
#if DEBUG
        true
#else
        false
#endif

    let isDebug() = isDebugConfiguration || Debugger.IsAttached


    // for the service registration in the database
    // (even after a timeout, it can be refreshed so long as no other service was started for this database)
    let ServiceHeartbeatInterval = TimeSpan.FromSeconds 10.
    let ServiceHeartbeatTimeout = TimeSpan.FromSeconds 35.
    
    // for the login in the User Service from TIM.
    // After a timeout, the session is forcibly closed on the server, and CAN'T be re-established,
    // that's why it is so long in debug.
    // So if you break in the debugger for 20 minutes, you won't be able to continue.
    let ClientHeartbeatInterval = TimeSpan.FromSeconds 5.
    let ClientHeartbeatTimeout =
        if isDebug() then
            TimeSpan.FromMinutes 20.
        else
            TimeSpan.FromSeconds 35.
    
    // how long it should wait for a response when Asking.
    let ActorCommunicationTimeout =
        if isDebug() then
            TimeSpan.FromMinutes 20.
        else
            TimeSpan.FromSeconds 20.

