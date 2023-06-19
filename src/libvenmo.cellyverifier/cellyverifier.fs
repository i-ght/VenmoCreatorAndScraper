namespace LibVenmo.CellyVerifier

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Collections.ObjectModel
open System.IO
open System.Linq
open System.Net
open System.Reactive.Linq
open System.Reactive.Subjects
open System.Threading

open LibNhk
open LibNhk.Http

open LibVenmo

open Newtonsoft.Json

[<AbstractClass>]
type UIUpdater () =
    abstract member UpdateId: id: string -> unit
    abstract member UpdateStatus: msg: string -> unit
    abstract member IncrementAttempts: amt: int64 -> unit
    abstract member IncrementVerified: amt: int64 -> unit
    abstract member IncrementOnline: unit -> unit
    abstract member DecrementOnline: unit -> unit

[<NoEquality; NoComparison>]
type Cfg =
    { MaxWorkers: int
      MinDelayBetweenVerifyAttempts: int
      MaxDelayBetweenVerifyAttempts: int
      MinContactsPerVerifySession: int
      MaxContactsPerVerifySession: int
      MinDelayBetweenVerifySession: int
      MaxDelayBetweenVerifySession: int
      MinContactsPerVerifyReq: int
      MaxContactsPerVerifyReq: int
      MaxTotalVerifyAttemptsPerAccount: int }
with
    member __.Validate () =
        let vals =
            struct (
                __.MaxWorkers,
                __.MinDelayBetweenVerifyAttempts,
                __.MaxDelayBetweenVerifyAttempts,
                __.MinContactsPerVerifySession,
                __.MaxContactsPerVerifySession,
                __.MinDelayBetweenVerifySession,
                __.MaxDelayBetweenVerifySession,
                __.MaxTotalVerifyAttemptsPerAccount,
                __.MinContactsPerVerifyReq,
                __.MaxContactsPerVerifyReq
            )
        match vals with
        | struct (LTE 0, _, _, _, _, _, _, _, _, _) -> invalidOp "max workers must be > 0."
        | struct (_, LT 0, _, _, _, _, _, _, _, _) -> invalidOp "min delay between verify attempts must be >= 0."
        | struct (_, _, LT 0, _, _, _, _, _, _, _) -> invalidOp "max delay between verify attempts must be >= 0."
        | struct (_, GT __.MaxDelayBetweenVerifyAttempts, _, _, _, _, _, _, _, _) -> invalidOp "min delay between verify attempts must be <= Max delay between verify attempts."
        | struct (_, _, _, LTE 0, _, _, _, _, _, _) -> invalidOp "min contacts per verify session must be > 0."
        | struct (_, _, _, _, LTE 0, _, _, _, _, _) -> invalidOp "max contacts per verify session must be > 0."
        | struct (_, _, _, GT __.MaxContactsPerVerifySession, _, _, _, _, _, _) -> invalidOp "min contacts per verify session must be < max attempts per verify session."
        | struct (_, _, _, _, _, LT 0, _, _, _, _) -> invalidOp "min delay between verify sessions must be >= 0."
        | struct (_, _, _, _, _, _, LT 0, _, _, _) -> invalidOp "max delay between verify sessions must be >= 0."
        | struct (_, _, _, _, _, _, GT __.MaxDelayBetweenVerifySession, _, _, _) -> invalidOp "min delay between verify sessions must be <= max delay between verify sessions."
        | struct (_, _, _, _, _, _, _, LTE 0, _, _) -> invalidOp "max total verify attempts per account must be > 0."
        | struct (_, _, _, _, _, _, _, _, LTE 0, _) -> invalidOp "min contacts per verify req must be > 0."
        | struct (_, _, _, _, _, _, _, _, _, LTE 0) -> invalidOp "max contacts per verify req must be > 0."
        | struct (_, _, _, _, _, _, _, _, GT __.MaxContactsPerVerifyReq, _) -> invalidOp "min contacts per verify req must be <= max contacts per verify req."
        | struct (_, _, _, _, _, _, _, _, GT 100, _) -> invalidOp "min contacts per verify req must be <= 100."
        | struct (_, _, _, _, _, _, _, _, _, GT 100) -> invalidOp "max contacts per verify req must be <= 100."
        | _ -> ()

[<NoEquality; NoComparison>]
type Sequences =
    { Sessions: Queue<Session>
      Proxies: Queue<WebProxy>
      ContactsStream: ConcurrentStreamReader
      FirstNames: Queue<string> }
with
    member __.Validate () =
        let vals = struct (__.Sessions, __.Proxies, __.ContactsStream)
        match vals with
        | struct (IsEmpty, _, _) -> invalidOp "sessions file does not exist, is empty or has invalid lines."
        | struct (_, IsEmpty, _) -> invalidOp "proxies file does not exist, is empty or has invalid lines."
        | struct (_, _, contacts) when contacts |> isNull || contacts.StreamReader.EndOfStream -> invalidOp "cellys file does not exist or is empty."
        | _ -> ()

module Agent =

    [<NoEquality; NoComparison>]
    type Props =
        { Index: int
          UIUpdater: UIUpdater
          CancellationToken: CancellationToken
          WriteWorker: WriteWorker
          ContactBlacklist: Blacklist }

    [<NoEquality; NoComparison>]
    type State =
        { Session: Session
          Cfg: Cfg
          Contacts: Queue<string>
          TotalVerifyAttempts: int
          FirstNames: Queue<string>
          IsInvalid: bool
          IsDeactivated: bool
          Status: string
          LastSessionEndedAt: DateTimeOffset
          StartNextSessionAt: DateTimeOffset
          Cts: CancellationTokenSource
          IsOnline: bool }

    [<Struct>]
    type DeactivateReason =
        | OutOfContacts
        | ReachedMaxAttempts

    [<NoEquality; NoComparison; Struct>]
    type Msg =
        | Activate
        | VerifyNumbers of numbers: string list * contacts: Contact list
        | Deactivate of reason: DeactivateReason
        | SetCfg of cfg: Cfg
        | SetFirstNames of firstNames: Queue<string>
        | DelayThenVerify
        | ValidateSession

    [<Struct; NoEquality; NoComparison>]
    type Ctx = { Props: Props; State: State; Agent: MailboxProcessor<Msg> }

    type AgentException (msg: string, inner: exn, ctx: Ctx) =
        inherit InvalidOperationException (msg, inner)
        member __.Ctx = ctx

    let execute (directive: Msg) (ctx: Ctx) = ctx.Agent.Post (directive); ctx

    let executeIn (seconds: int) (directive: Msg) (ctx: Ctx) =
        let agent = ctx.Agent
        Async.Start (
            async {
                do! Async.Sleep (seconds * 1000)
                agent.Post (directive)
            }
        , ctx.State.Cts.Token)
        ctx

    let deserialize<'a> o =
        let result = JsonConvert.DeserializeObject<'a> (o)
        if result |> isNull then JsonException ("json deserialization returned unexpected null value.") |> raise
        result

    let deactivate (ctx: Ctx) = { ctx with State = { ctx.State with IsDeactivated = true } }

    let session ctx = ctx.State.Session

    let notAuth () = SessionNotAuthorizedException ("session not authorized. venmo api server returned http status code not authorized.") |> raise

    let contentBody (resp: Response) =
        match resp.ContentBody.Length with
        | LTE 256 -> resp.ContentBody
        | _ -> resp.ContentBody.Substring (0, 256)

    let validateSession (ctx: Ctx) =
        async {
            let! resp = session ctx |> ApiClient.me
            match resp with
            | ExpectedStatusCode HttpStatusCode.OK ->
                ctx.Props.UIUpdater.IncrementOnline ()
                return { ctx with State = { ctx.State with IsOnline = true } }
            | ExpectedStatusCode HttpStatusCode.Unauthorized -> return notAuth ()
            | ExpectedContentBody "\"error\":" -> return deserialize<ApiErrResp> resp.ContentBody |> sprintf "venmo api returned error: %A" |> invalidOp
            | _ -> return invalidOp <| sprintf "unexpected response received from venmo api server after attempting to validate session. status code was '%O'. content body was '%s'." resp.StatusCode (contentBody resp)
        }

    let firstName ctx =
        lock ctx.State.FirstNames <| fun _ ->
            let item = ctx.State.FirstNames.Dequeue ()
            ctx.State.FirstNames.Enqueue (item)
            item

    let contacts ctx =
        let state = ctx.State
        let struct (cfg, contacts) = struct (state.Cfg, state.Contacts)
        let amt = Random.next cfg.MinContactsPerVerifyReq cfg.MaxContactsPerVerifyReq
        let amt =
            match amt with
            | GT contacts.Count -> contacts.Count
            | _ -> amt

        let c = ResizeArray<Contact> (amt)
        let numbers = ResizeArray<string> (amt)
        let rec dequeue index =
            match index with
            | GTE amt -> ()
            | _->
                let firstName = firstName ctx
                let number = contacts.Dequeue ()
                let contact: Contact =
                    { Name = firstName
                      Index = index |> string
                      CellyNumbers = [number]
                      Emails = [] }
                c.Add (contact)
                numbers.Add (number)
                dequeue (index + 1)
        dequeue 0
        struct (List.ofSeq c, List.ofSeq numbers)

    let validateSessionMsg (ctx: Ctx) =
        async {
            let! ctx = validateSession ctx
            let struct (contacts, numbers) = contacts ctx
            match contacts with
            | [] -> return execute (Deactivate OutOfContacts) ctx
            | _ -> return execute (VerifyNumbers (numbers, contacts)) ctx
        }

    let enqueueContacts (contacts: Contact seq) ctx =
        let enqueue (c: Contact) =
            let enqueue (number: string) =
                ctx.State.Contacts.Enqueue (number)
            Seq.iter enqueue c.CellyNumbers
        Seq.iter enqueue contacts

    let activate ctx = execute ValidateSession ctx

    type DataContainer<'a> =
        { Data: 'a }

    let parse (resp: Response) (numbers: ReadOnlyCollection<string>) (ctx: Ctx) =
        let results =
            (deserialize<DataContainer<ReadOnlyDictionary<string, UserInfo>>> (resp.ContentBody)).Data
        let results =
            results |> Seq.map (fun (kvp: KeyValuePair<string, UserInfo>) -> UserInfoWithNumber.OfUserInfo (numbers.[kvp.Key |> int], kvp.Value))
            |> List.ofSeq
        let props = ctx.Props
        if results.Length > 0 then
            let csvRows =
                Seq.map (fun (info: UserInfoWithNumber) -> info.ToCsvRow ()) results
                |> List.ofSeq
            props.WriteWorker.Add (csvRows)
            props.UIUpdater.IncrementVerified (results.Length |> int64)
        props.ContactBlacklist.ThreadSafeAdd (numbers)

    let incrTtl (numbers: string list) ctx =
        let state = ctx.State
        let state =  { state with TotalVerifyAttempts = state.TotalVerifyAttempts + numbers.Length }
        { ctx with State = state }

    let verify (numbers: string list) (contacts: Contact list) (ctx: Ctx) =
        let handleEx (e: exn) = enqueueContacts contacts ctx; raise' e
        async {
            try
                let! resp = ApiClient.syncContacts contacts <| session ctx
                match resp with
                | ExpectedStatusCode HttpStatusCode.OK ->
                    ctx.Props.UIUpdater.IncrementAttempts (numbers.Length |> int64)
                    parse resp (ReadOnlyCollection.ofSeq numbers) ctx
                    return incrTtl numbers ctx
                | ExpectedStatusCode HttpStatusCode.Unauthorized -> return notAuth ()
                | ExpectedContentBody "\"error\":" -> return deserialize<ApiErrResp> resp.ContentBody |> sprintf "venmo api returned error: %A" |> invalidOp
                | _ -> return invalidOp <| sprintf "unexpected response received from venmo api server after attempting to verify contacts. status code was '%O'. content body was '%s'." resp.StatusCode (contentBody resp)
            with
            | :? JsonException
            | :? InvalidOperationException
            | :? IOException
            | :? TimeoutException as e -> return handleEx e
        }

    let agentEx (msg: string) (inner: exn) (ctx: Ctx) = AgentException (msg, inner, ctx) |> raise

    let exec fn ctx =
        async {
            try return! fn ctx
            with
            | :? InvalidOperationException
            | :? IOException
            | :? TimeoutException as e -> return agentEx "Agent exception occured." e ctx
            | e -> return failFast (sprintf "unhandled exception in process agent message. %s ~ %s" (e.GetType().Name) e.Message) e
        }

    let updateStatus status ctx =
        ctx.Props.UIUpdater.UpdateStatus (status)
        { ctx with State = { ctx.State with Status = status } }

    let updateId ctx = ctx.Props.UIUpdater.UpdateId (ctx.State.Session.AuthVal.Email); ctx

    let l = obj ()
    let logExn (ex: exn) = lock l <| fun _ -> appendLine  "errors.txt" (ex.ToDetailedInfoString ())

    let handleEx (ex: exn) (ctx: Ctx) =
        logExn ex
        match ex with
        | :? SessionNotAuthorizedException -> { ctx with State = { ctx.State with IsInvalid = true } }
        | _ -> ctx
        |> updateStatus (sprintf "%s: failed. %s ~ %s" ctx.State.Status (ex.GetType().Name) ex.Message)

    let setCfg cfg ctx = { ctx with State = { ctx.State with Cfg = cfg } }
    let setFirstNames firstNames ctx = { ctx with State = { ctx.State with FirstNames = firstNames } }

    let delaySeconds ctx =
        let cfg = ctx.State.Cfg
        Random.next cfg.MinDelayBetweenVerifyAttempts cfg.MaxDelayBetweenVerifyAttempts

    let delayVerify seconds ctx =
        async {
            let struct (contacts, numbers) = contacts ctx
            match contacts with
            | [] -> return execute (Deactivate OutOfContacts) ctx
            | _ -> return executeIn seconds (VerifyNumbers (numbers, contacts)) ctx
        }

    let verifyNumbers numbers contacts ctx =
        async {
            let! ctx = verify numbers contacts ctx
            return execute DelayThenVerify ctx
        }

    type Agent (props: Props, state: State, onObjCompleted: Ctx -> unit, onObjFailed: Ctx -> exn -> unit) =
        let mutable disposed = false

        let proc msg ctx =
            async {
                match msg with
                | Activate -> return updateId ctx |> activate
                | VerifyNumbers (numbers, contacts) ->
                    return!
                        updateStatus (sprintf "verifying %d contacts: ..." numbers.Length) ctx
                        |> exec (verifyNumbers numbers contacts)
                | Deactivate OutOfContacts | Deactivate ReachedMaxAttempts ->
                    let ctx =  updateStatus (sprintf "session completed. total verify attempts = %d" ctx.State.TotalVerifyAttempts) ctx
                    return { ctx with State = { ctx.State with IsDeactivated = true } } //TODO
                | SetCfg cfg -> return setCfg cfg ctx
                | SetFirstNames firstNames -> return setFirstNames firstNames ctx
                | DelayThenVerify ->
                    let seconds = delaySeconds ctx
                    return! updateStatus (sprintf "delaying %d seconds before next request: ..." seconds) ctx |> delayVerify seconds
                | ValidateSession ->
                    return! updateStatus "validating session: ..." ctx |> exec validateSessionMsg
            }

        let handleAgentEx (e: AgentException) =
            let innerEx = e.InnerException
            let ctx = handleEx innerEx e.Ctx
            onObjFailed ctx innerEx

        let start (agent: MailboxProcessor<Msg>) =
            let rec recv ctx =
                async {
                    try
                        let! msg = agent.Receive ()
                        let! ctx = proc msg ctx
                        match ctx.State.IsDeactivated with
                        | true -> return onObjCompleted ctx
                        | false -> return! recv ctx
                    with
                    | :? AgentException as e -> return handleAgentEx e
                    | e -> return failFast (sprintf "unhandled exception in agent receive loop. %s ~ %s" (e.GetType().Name) e.Message) e
                }
            async {
                let ctx = { Props=props; State=state; Agent=agent }
                return! recv ctx
            }

        let agent = new MailboxProcessor<Msg> (start, props.CancellationToken)
        let onErr = Observable.subscribe (fun e -> failFast "Unhandled exception in Agent" e) agent.Error

        do agent.Start ()

        member __.SetCfg (cfg: Cfg) = SetCfg cfg |> agent.Post
        member __.SetFirstNames (firstNames: Queue<string>) = SetFirstNames firstNames |> agent.Post
        member __.Activate () = agent.Post (Activate)
        member __.IsDisposed = disposed

        interface IDisposable with
            override __.Dispose () =
                if not disposed then
                    disposeSeq [agent; onErr]
                    disposed <- true

module Director =

    open Agent

    [<NoEquality; NoComparison>]
    type Props =
        { UIUpdaters: IReadOnlyDictionary<int, UIUpdater>
          CancellationToken: CancellationToken
          WriteWorker: WriteWorker
          ContactsBlacklist: Blacklist }

    [<NoEquality; NoComparison>]
    type AgentContainer =
        { Props: Agent.Props
          State: Agent.State
          Agent: Agent }

    [<NoEquality; NoComparison>]
    type State =
        { ActiveWorkers: int
          Cfg: Cfg
          Sequences: Sequences
          Agents: Dictionary<string, AgentContainer> }

    [<Struct>]
    type Msg =
        | ActivateAgent of index: int
        | ObjectiveFailed of ex: exn * ctx: Agent.Ctx
        | ObjectiveCompleted of ctx2: Agent.Ctx
        | SetCfg of cfg: Cfg
        | GetCfg of channel: AsyncReplyChannel<Cfg>
        | SetSeqs of seqs: Sequences
        | GetSeqs of channel2: AsyncReplyChannel<Sequences>

    [<Struct; NoEquality; NoComparison>]
    type Ctx =
        { Props: Props
          State: State
          Agent: MailboxProcessor<Msg> }

    let sequences (channel: AsyncReplyChannel<_>) ctx = channel.Reply (ctx.State.Sequences); ctx
    let setSequences (sequences: Sequences) ctx =
        let updateSeqs (container: AgentContainer) = if not container.Agent.IsDisposed then container.Agent.SetFirstNames (sequences.FirstNames)
        let fn1 = HashSet<string> (ctx.State.Sequences.FirstNames)
        let fn2 = HashSet<string> (sequences.FirstNames)
        if fn1.SequenceEqual (fn2) then
            Seq.iter updateSeqs ctx.State.Agents.Values
        { ctx with State = { ctx.State with Sequences = sequences } }: Ctx

    let private dequeue (q: Queue<'a>) (requeue: bool) =
        let item = q.Dequeue ()
        if requeue then q.Enqueue item
        item

    // objective is to use sessions until contactsstream is empty
    let (|ObjectiveNotCompleted|_|) (ctx: Ctx) =
        let seqs = ctx.State.Sequences
        match struct (seqs.Sessions, seqs.ContactsStream) with
        | struct (IsEmpty, _) -> None
        | struct (_, c) when c.EndOfStream -> None
        | _ -> Some ()

    let (|ShouldDispatchAgent|_|) (agentState: Agent.State) =
        match agentState.StartNextSessionAt with
        | GTE DateTimeOffset.Now -> None
        | _ -> Some ()

    let (|AgentIsExhausted|_|) (cfg: Cfg) (agentState: Agent.State) =
        match struct (agentState.IsInvalid, agentState.TotalVerifyAttempts) with
        | struct (true, _) | struct (_, GTE cfg.MaxTotalVerifyAttemptsPerAccount) -> Some ()
        | _ -> None

    let execute msg (agent: MailboxProcessor<_>) = agent.Post (msg)

    let executeIn (seconds: int) (directive: Msg) (ctx: Ctx) =
        let agent = ctx.Agent
        Async.Start (
            async {
                do! Async.Sleep (seconds * 1000)
                agent.Post (directive)
            }
        , ctx.Props.CancellationToken)
        ctx

    let contactsThisSession ctx =
        let state = ctx.State
        let cfg = state.Cfg
        Random.next cfg.MinContactsPerVerifySession cfg.MaxContactsPerVerifySession

    let contacts ctx =
        let state = ctx.State
        let sequences = state.Sequences
        let contactsThisSession = contactsThisSession ctx
        let lst = ResizeArray<string> (contactsThisSession)
        let contactsStream = sequences.ContactsStream
        let blacklist = ctx.Props.ContactsBlacklist

        let rec loop () =
            match lst.Count with
            | GTE contactsThisSession -> ()
            | _ ->
                match contactsStream.NotThreadSafeTryReadLine () with
                | ValueNone -> ()
                | ValueSome line ->
                match blacklist.Contains (line) with
                | true -> loop ()
                | false ->
                    lst.Add (line)
                    loop ()

        loop ()
        Queue.ofSeq lst

    let uiUpdater index ctx = ctx.Props.UIUpdaters.[index]

    let activateAgent index ctx =
        let state = ctx.State
        let cfg = state.Cfg
        let sequences = state.Sequences
        match ctx with
        | ObjectiveNotCompleted -> // activate agent to achieve objective
            let props = ctx.Props
            let agentProps: Agent.Props =
                { Index = index
                  UIUpdater = uiUpdater index ctx
                  CancellationToken = props.CancellationToken
                  WriteWorker = props.WriteWorker
                  ContactBlacklist = props.ContactsBlacklist }

            let session = dequeue sequences.Sessions false
            let email = session.AuthVal.Email

            let agentState =
                match state.Agents with
                | Dict.ContainsKey email a -> a.State
                | _ ->
                    { Session = session
                      Cfg = cfg
                      Contacts = Queue.ofSeq []
                      TotalVerifyAttempts = 0
                      FirstNames = sequences.FirstNames
                      IsInvalid = false
                      IsOnline = false
                      IsDeactivated = false
                      Status = ""
                      LastSessionEndedAt = DateTimeOffset.MinValue
                      StartNextSessionAt = DateTimeOffset.MinValue
                      Cts = null }: Agent.State

            match agentState with
            | ShouldDispatchAgent -> // agent has delayed long enough according to cfg.
                let proxy = dequeue sequences.Proxies true
                let session =  { session.AuthVal with Proxy = proxy |> ValueSome } |> Auth
                let contacts = contacts ctx
                let agentState =
                    { agentState with
                        Session = session
                        Contacts = contacts
                        IsDeactivated = false
                        Cfg = cfg
                        FirstNames = sequences.FirstNames
                        Cts = new CancellationTokenSource ()}
                let director = ctx.Agent
                let objComp agentCtx = execute <| ObjectiveCompleted agentCtx <| director
                let objFailed agentCtx ex = execute <| ObjectiveFailed (ex, agentCtx) <| director
                let agent = new Agent (agentProps, agentState, objComp, objFailed)
                agent.Activate ()
                let cont: AgentContainer =
                    { State = agentState
                      Props = agentProps
                      Agent = agent }
                match state.Agents with
                | Dict.ContainsKey session.AuthVal.Email _ -> state.Agents.[email] <- cont
                | _ -> state.Agents.Add (email, cont)
                ctx
            | _ -> // agent needs to wait according to config
                agentProps.UIUpdater.UpdateId (email)
                agentProps.UIUpdater.UpdateStatus (sprintf "will start next session @ %s." (agentState.StartNextSessionAt.ToString ("t")))
                ctx.State.Sequences.Sessions.Enqueue (session)
                executeIn 9 (ActivateAgent index) ctx
        | _ -> { ctx with State = { state with ActiveWorkers = state.ActiveWorkers - 1 } }

    let disposeAgent (agentState: Agent.State) ctx =
        let state = ctx.State
        let email = agentState.Session.AuthVal.Email
        let cont = state.Agents.[email]
        let agent = cont.Agent
        disposeSeq [agentState.Cts; agent]
        //update state in agents dict to new state from agent
        state.Agents.[email] <- { state.Agents.[email] with State = agentState }
        ctx

    let nextSession ctx =
        let cfg = ctx.State.Cfg
        let mins = Random.next cfg.MinDelayBetweenVerifySession cfg.MaxDelayBetweenVerifySession |> float
        DateTimeOffset.Now + TimeSpan.FromMinutes (mins)

    let debriefAgent (agentCtx: Agent.Ctx) (ctx: Ctx) =
        let struct (agentProps, agentState) = struct (agentCtx.Props, agentCtx.State)
        if agentState.IsOnline then agentProps.UIUpdater.DecrementOnline ()
        let struct (cfg, seqs) = struct (ctx.State.Cfg, ctx.State.Sequences)
        match agentState with
        | AgentIsExhausted cfg -> ()
        | _ -> seqs.Sessions.Enqueue (agentState.Session)
        match agentState.Contacts with
        | IsEmpty -> ()
        | _ ->
            let enqueue contact = ctx.State.Sequences.ContactsStream.EnqueueLine (contact)
            let rec dequeue () =
                match agentState.Contacts with
                | IsEmpty -> ()
                | _ -> agentState.Contacts.Dequeue () |> enqueue; dequeue ()
            dequeue ()
        let agentState =
            { agentState with
                IsOnline = false
                LastSessionEndedAt = DateTimeOffset.Now
                StartNextSessionAt = nextSession ctx }
        disposeAgent agentState ctx

    let cfg (channel: AsyncReplyChannel<_>) ctx = channel.Reply (ctx.State.Cfg); ctx
    let setCfg cfg ctx =
        let updateCfg (container: AgentContainer) = if not container.Agent.IsDisposed then container.Agent.SetCfg (cfg)
        Seq.iter updateCfg ctx.State.Agents.Values
        { ctx with State = { ctx.State with Cfg = cfg } }: Ctx

    let objCompleted (agentCtx: Agent.Ctx) (ctx: Ctx) =
        debriefAgent agentCtx ctx
        |> executeIn 9 (ActivateAgent agentCtx.Props.Index)

    let objFailed (_: exn) (agentCtx: Agent.Ctx) (ctx: Ctx) =
        debriefAgent agentCtx ctx
        |> executeIn 9 (ActivateAgent agentCtx.Props.Index)

    type Director (props: Props, state: State) =
        let mutable disposed = false
        let terminated = new Subject<unit> ()

        let proc msg ctx =
            match msg with
            | GetCfg channel -> cfg channel ctx
            | SetCfg cfg -> setCfg cfg ctx
            | GetSeqs channel -> sequences channel ctx
            | SetSeqs seqs -> setSequences seqs ctx
            | ActivateAgent index -> activateAgent index ctx
            | ObjectiveCompleted agentCtx -> objCompleted agentCtx ctx
            | ObjectiveFailed (ex, agentCtx) -> objFailed ex agentCtx ctx

        let start (agent: MailboxProcessor<Msg>) =
            let rec recv ctx =
                async  {
                    let state = ctx.State
                    match state.ActiveWorkers with
                    | LTE 0 ->
                        terminated.OnNext ()
                        terminated.OnCompleted ()
                        return ()
                    | _ ->
                        let! msg = agent.Receive ()
                        let ctx = proc msg ctx
                        return! recv ctx
                }
            async {
                let ctx = { Props=props; State=state; Agent=agent }
                return! recv ctx
            }

        let agent = new MailboxProcessor<Msg> (start, props.CancellationToken)
        let onErr = Observable.subscribe (fun (e: exn) -> failFast (sprintf "unhandled exception in Director: %s ~ %s" (e.GetType().Name) e.Message) e) agent.Error

        do agent.Start ()

        member __.Terminated = terminated.AsObservable ()

        member __.Sequences
            with get () = agent.PostAndReply <| fun channel -> GetSeqs channel
            and set value = SetSeqs value |> agent.Post

        member __.Cfg
            with get () = agent.PostAndReply <| fun channel -> GetCfg channel
            and set value = SetCfg value |> agent.Post

        member __.Activate () =
            let rec activate i =
                match i with
                | GTE state.Cfg.MaxWorkers -> ()
                | _ -> ActivateAgent i |> agent.Post; activate (i + 1)
            activate 0

        interface IDisposable with
            override __.Dispose () =
                if not disposed then
                    disposeSeq [onErr; agent; terminated]
                    disposed <- true