namespace LibVenmo.Creator

open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Reactive.Linq
open System.Reactive.Subjects
open System.Text
open System.Threading

open LibNhk
open LibNhk.Http

open LibVenmo

open Newtonsoft.Json

[<AbstractClass>]
type UIUpdater () =
    abstract member UpdateId: id: string -> unit
    abstract member UpdateStatus: msg: string -> unit
    abstract member IncrementAttempts: unit -> unit
    abstract member IncrementCreated: unit -> unit

type PvaProviderType =
    | Console

[<AbstractClass; AllowNullLiteral>]
type CellyCodeRetriever(celly: string) =
    member _.Celly = celly
    abstract member TryGetCodeAsync: unit -> Async<string voption>

[<AbstractClass; AllowNullLiteral>]
type CellySeq() =
    abstract member TryGetNextAsync: unit -> Async<CellyCodeRetriever voption>

type ConsoleCodeRetriever(celly: string) =
    inherit CellyCodeRetriever(celly)
    override _.TryGetCodeAsync() = async {
        stdout.WriteLine("enter code for celly {0}:", celly)
        let! line = stdin.ReadLineAsync() |> Async.AwaitTask
        return ValueSome line
    }

type ConsoleCellySeq() =
    inherit CellySeq()
    override _.TryGetNextAsync() = async {
        stdout.WriteLine("enter celly number:")
        let! line = stdin.ReadLineAsync() |> Async.AwaitTask
        return ConsoleCodeRetriever(line) :> CellyCodeRetriever |> ValueSome
    }

[<NoEquality; NoComparison>]
type Cfg =
    { MaxWorkers: int
      MaxCreates: int
      DelayBetweenWorkers: int
      PvaProviderType: PvaProviderType }
with
    member __.Validate () =
        match struct (__.MaxWorkers, __.MaxCreates) with
        | struct (LTE 0, _) -> invalidOp "max workers must be > 0."
        | struct (_, LTE 0) -> invalidOp "max creates must be > 0."
        | _ -> ()

[<NoEquality; NoComparison>]
type Collections =
    { Proxies: Queue<WebProxy>
      Devices: Queue<AndroidDevice>
      FirstNames: Queue<string>
      LastNames: Queue<string>
      AreaCodes: Queue<string>
      CellySeq: CellySeq }
with
    member __.Validate () =
        match struct (__.Proxies, __.Devices, __.FirstNames, __.LastNames, __.AreaCodes) with
        | struct (IsEmpty, _, _, _, _) -> invalidOp "proxies file does not exist, is empty or has invalid lines."
        | struct (_, IsEmpty, _, _, _) -> invalidOp "devices file does not exist, is empty or has invalid lines."
        | struct (_, _, IsEmpty, _, _) -> invalidOp "first names file does not exist or is empty."
        | struct (_, _, _, IsEmpty, _) -> invalidOp "last names file does not exist or is empty."
        | struct (_, _, _, _, IsEmpty) -> invalidOp "area codes file does not exist or is emppty."
        | _ -> ()

module Agent =

    [<NoEquality; NoComparison>]
    type Props =
        { Index: int
          UIUpdater: UIUpdater
          CancellationToken: CancellationToken }

    [<NoEquality; NoComparison>]
    type State =
        { Cfg: Cfg
          Status: string
          FirstName: string
          LastName: string
          Email: string
          Password: string
          PhoneNumber: string
          Session: Session
          Deactivated: bool 
          CellySeq: CellySeq 
          mutable CellyCodeRetr: CellyCodeRetriever
          mutable Code: string }

    [<Struct>]
    type Msg =
        | Activate
        | GetNumber
        | UpdateNumber
        | Register
        | GetCode
        | ValidateCode

    [<Struct; NoEquality; NoComparison>]
    type Ctx = { Props: Props; State: State; Agent: MailboxProcessor<Msg> }

    type AgentException (msg: string, inner: exn, ctx: Ctx) =
        inherit InvalidOperationException (msg, inner)
        member __.Ctx = ctx

    let execute (directive: Msg) (ctx: Ctx) = ctx.Agent.Post (directive); ctx

    let activate (ctx: Ctx) = execute Register ctx

    let contentBody (resp: Response) =
        match resp.ContentBody.Length with
        | LTE 256 -> resp.ContentBody
        | _ -> resp.ContentBody.Substring (0, 256)

    let parseJson<'a> (resp: Response) (statCode: HttpStatusCode) (chars: string) (validate: 'a -> 'a) (id: string) =
        match resp with
        | Expected statCode chars -> Json.deserialize<'a> resp.ContentBody |> validate
        | ExpectedContentBody "\"error\":" -> Json.deserialize<ApiErrResp> resp.ContentBody |> sprintf "venmo api returned error: %A" |> invalidOp
        | _ -> invalidOp <| sprintf "unexpected response received from venmo api server after sending '%s' request. status code was %O. content body was %s" id resp.StatusCode (contentBody resp)

    let getNumber (ctx: Ctx) = async {
        match! ctx.State.CellySeq.TryGetNextAsync() with
        | ValueNone -> invalidOp "out of numbers"
        | ValueSome value -> ctx.State.CellyCodeRetr <- value
        ctx.Agent.Post(Register)
        return ctx
    }

    let getCode (ctx: Ctx) = async {
        match! ctx.State.CellyCodeRetr.TryGetCodeAsync() with
        | ValueNone -> invalidOp "failed to get sms code"
        | ValueSome value -> ctx.State.Code <- value
        ctx.Agent.Post(ValidateCode)
        return ctx
    }

    let register (ctx: Ctx) =
        async {
            let state = ctx.State
            let info: RegisterInfo =
                { FirstName = state.FirstName
                  LastName = state.LastName
                  Phone = state.PhoneNumber
                  ClientId = 4
                  Password = state.Password
                  Email = state.Email }
            let! resp = ApiClient.register info state.Session
            let validate (data: RegRespContainer) =
                let json = data.Data
                match struct (json.AccessToken, json.User.Id) with
                | struct (IsNullOrWhiteSpace, _) -> invalidOp "expected venmo api register response to not have empty access_token."
                | struct (_, IsNullOrWhiteSpace) -> invalidOp "expected venmo api register response to not have empty user_id."
                | _ -> data
            let json = parseJson<RegRespContainer> resp HttpStatusCode.Created "access_token" validate "register"
            let state = ctx.State
            let anonSession = state.Session.AnonVal
            let authSession: AuthSession =
                { AccessToken = json.Data.AccessToken
                  UserId = json.Data.User.Id
                  DeviceId = anonSession.DeviceId
                  Proxy = anonSession.Proxy
                  Email = state.Email
                  Password = state.Password
                  PhoneNumber = state.PhoneNumber
                  Device = anonSession.Device }
            //ctx.Agent.Post(UpdateNumber)
            return { ctx with State = { state with Session = authSession |> Auth; Deactivated=true} }
        }

    let validateCode (ctx: Ctx) = async {
        let! resp = ApiClient.validateCode ctx.State.CellyCodeRetr.Celly ctx.State.Code ctx.State.Session
        if resp.StatusCode <> HttpStatusCode.OK then
            return invalidOp <| sprintf "validate code failed: server returned %O" resp.StatusCode
        return { ctx with State = { ctx.State with Deactivated=true } }
    }

    let updateNumber (ctx: Ctx) = async {
        let! resp = ApiClient.updateNumber ctx.State.CellyCodeRetr.Celly ctx.State.Session
        if resp.StatusCode <> HttpStatusCode.Created then
            return invalidOp <| sprintf "update number failed: server returned %O" resp.StatusCode
        ctx.Agent.Post(GetCode)
        return ctx
    }

    let updateStatus status ctx =
        ctx.Props.UIUpdater.UpdateStatus (status)
        { ctx with State = { ctx.State with Status = status } }

    let updateId ctx = ctx.Props.UIUpdater.UpdateId (ctx.State.PhoneNumber); ctx

    let agentEx (msg: string) (inner: exn) (ctx: Ctx) = AgentException (msg, inner, ctx) |> raise

    let exec fn ctx =
        async {
            try
                return! fn ctx
            with
            | :? JsonException
            | :? InvalidOperationException
            | :? IOException
            | :? TimeoutException as e -> return agentEx "Agent exception occured." e ctx
            | e -> return failFast (sprintf "unhandled exception in process agent message. %s ~ %s" (e.GetType().Name) e.Message) e
        }

    let lock' = obj ()
    let logExn (ex: exn) =
        lock lock' <| fun _ ->
            appendLine "errors.txt" (ex.ToDetailedInfoString ())

    let handleEx (e: exn) (ctx: Ctx) =
        logExn e
        updateStatus (sprintf "%s Failed. %s ~ %s" (ctx.State.Status) (e.GetType().Name) e.Message) ctx

    type Agent (props: Props, state: State, onObjectiveCompleted: Ctx -> unit, onObjectiveFailed: exn -> Ctx -> unit) =

        let mutable disposed = false

        let proc msg ctx =
            async {
                match msg with
                | Activate -> return activate ctx
                | GetNumber -> return! updateStatus "getting number: ..." ctx |> exec getNumber
                | Register -> return! updateStatus "attempting registration: ..." ctx |> exec register
                | UpdateNumber -> return! updateStatus "updating number: ..." ctx |> exec updateNumber
                | GetCode -> return! updateStatus "getting code: ..." ctx |> exec getCode
                | ValidateCode -> return! updateStatus "validating code: ..." ctx |> exec validateCode
            }

        let start (agent: MailboxProcessor<Msg>) =
            let rec recv ctx =
                async {
                    try
                        let! msg = agent.Receive ()
                        let! ctx = proc msg ctx
                        match ctx.State.Deactivated with
                        | true ->
                            ctx.Props.UIUpdater.IncrementCreated ()
                            return updateStatus "account created" ctx |> onObjectiveCompleted
                        | false -> return! recv ctx
                    with
                    | :? AgentException as e ->
                        let ctx = handleEx e.InnerException e.Ctx
                        return onObjectiveFailed e.InnerException ctx
                    | e -> return failFast (sprintf "unhandled exception occured in Agent recv loop: %s ~ %s" (e.GetType().Name) e.Message) e
                }

            async {
                let ctx = { Props=props; State=state; Agent = agent } |> updateId
                ctx.Props.UIUpdater.IncrementAttempts ()
                return! recv ctx
            }

        let agent = new MailboxProcessor<Msg> (start, props.CancellationToken)
        let onErr = Observable.subscribe (fun (e: exn) -> failFast (sprintf "unhandled exception occured in Agent: %s ~ %s" (e.GetType().Name) e.Message) e) agent.Error
        do agent.Start ()

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
          CancellationToken: CancellationToken }

    [<NoEquality; NoComparison>]
    type State =
        { ActiveWorkers: int
          Cfg: Cfg
          Collections: Collections
          Created: int }

    [<Struct>]
    type Msg =
        | ActivateAgent of index: int
        | ObjectiveFailed of ex: exn * ctx: Agent.Ctx
        | ObjectiveCompleted of ctx2: Agent.Ctx
        | SetCfg of cfg: Cfg
        | GetCfg of channel: AsyncReplyChannel<Cfg>
        | SetCollections of collections: Collections
        | GetCollections of channel2: AsyncReplyChannel<Collections>

    [<Struct; NoEquality; NoComparison>]
    type Ctx =
        { Props: Props
          State: State
          Agent: MailboxProcessor<Msg> }

    let cfg (channel: AsyncReplyChannel<_>) ctx = channel.Reply (ctx.State.Cfg); ctx
    let setCfg cfg ctx = { ctx with State = { ctx.State with Cfg = cfg } }: Ctx
    let collections (channel: AsyncReplyChannel<_>) ctx = channel.Reply (ctx.State.Collections); ctx
    let setCollections collections ctx = { ctx with State = { ctx.State with Collections = collections } }: Ctx

    let dequeue (q: Queue<'a>) (requeue: bool) =
        let item = q.Dequeue ()
        if requeue then q.Enqueue item
        item

    let (|ShouldActivateAgent|_|) (ctx: Ctx) =
        let state = ctx.State
        match state.Created with
        | GTE state.Cfg.MaxCreates -> None
        | _ -> Some ()

    let execute directive (agent: MailboxProcessor<_>) = agent.Post (directive)

    let emailServices = ReadOnlyCollection.ofSeq ["gmail.com"; "outlook.com"; "hotmail.com"; "yahoo.com"]

    [<Struct>]
    type GenEmailKind =
        | FirstLast
        | FirstRand
        | RandLast
        | Rand
    with
        static member SelectRandom () =
            match Random.next 0 5 with
            | 0 -> FirstLast
            | 1 -> FirstRand
            | 2 -> RandLast
            | 3 -> Rand
            | _ -> FirstLast

    let email firstName lastName =
        let genKind = GenEmailKind.SelectRandom ()
        match genKind with
        | FirstLast -> sprintf "%s%s%d@%s" firstName lastName (Random.next 0 1000) (Seq.random emailServices)
        | FirstRand -> sprintf "%s%s@%s" firstName (String.random (Random.next 6 11) StringChars.LowerLetters) (Seq.random emailServices)
        | RandLast -> sprintf "%s%s@%s" (String.random (Random.next 6 11) StringChars.LowerLetters) lastName (Seq.random emailServices)
        | Rand -> sprintf "%s@%s" (String.random (Random.next 6 11) StringChars.LowerLetters) (Seq.random emailServices)

    let password () = String.random (Random.next 8 17) StringChars.DigitsAndLowerLetters

    let phoneNumber areaCode = sprintf "%s%s" areaCode (String.random 7 StringChars.Digits)

    let name (q: Queue<string>) =
        let firstName = dequeue q true
        let sb = StringBuilder (firstName.Length)
        let rec iter (ch: char) =
            if Char.IsLetterOrDigit (ch) then
                sb.Append (ch) |> ignore
        String.iter iter firstName
        sb.ToString ()

    let activateAgent index ctx =
        let state = ctx.State
        let cfg = state.Cfg
        let collections = state.Collections
        match ctx with
        | ShouldActivateAgent ->
            let struct (proxy, device, firstName, lastName, areaCode) =
                struct (
                    dequeue collections.Proxies true,
                    dequeue collections.Devices true,
                    name collections.FirstNames,
                    name collections.LastNames,
                    dequeue collections.AreaCodes true
                )

            let struct (email, password, phoneNumber) =
                struct (
                    email firstName lastName,
                    password (),
                    phoneNumber areaCode
                )

            let session: Session =
                { Device = device
                  Proxy = proxy |> ValueSome
                  DeviceId = Guid.NewGuid () |> string }
                |> Anon

            let props = ctx.Props
            let agentProps : Agent.Props =
                { Index = index
                  UIUpdater = props.UIUpdaters.[index]
                  CancellationToken = props.CancellationToken }

            let agentState: Agent.State =
                { Cfg = cfg
                  Status = ""
                  FirstName = firstName
                  LastName = lastName
                  Email = email
                  Password = password
                  PhoneNumber = phoneNumber
                  Session = session
                  CellySeq = ctx.State.Collections.CellySeq
                  CellyCodeRetr = null
                  Code = null
                  Deactivated = false }

            let director = ctx.Agent
            let objectiveCompleted agentCtx = execute <| ObjectiveCompleted agentCtx <| director
            let objectiveFailed agentCtx ex = execute  <| ObjectiveFailed (agentCtx, ex) <| director

            let agent = new Agent (agentProps, agentState, objectiveCompleted, objectiveFailed)
            agent.Activate ()

            ctx
        | _ -> { ctx with State = { ctx.State with ActiveWorkers = ctx.State.ActiveWorkers - 1 } }

    let activateAgentDelayed index ctx =
        let agent = ctx.Agent
        Async.Start (
            async {
                do! Async.Sleep (ctx.State.Cfg.DelayBetweenWorkers * 1000)
                agent.Post (ActivateAgent index)
            }, ctx.Props.CancellationToken
        )

    let objectiveCompleted (agentCtx: Agent.Ctx) (ctx: Ctx) =
        appendLine "venmo-sessions.txt" agentCtx.State.Session.AuthVal
        activateAgentDelayed agentCtx.Props.Index ctx
        { ctx with State = { ctx.State with Created = ctx.State.Created + 1 } }

    let objectiveFailed (ex: exn) (agentCtx: Agent.Ctx) (ctx: Ctx) =
        activateAgentDelayed agentCtx.Props.Index ctx
        ctx

    type Director (props: Props, state: State) =
        let mutable disposed = false
        let terminated = new Subject<unit> ()

        let proc msg ctx =
            match msg with
            | GetCfg channel -> cfg channel ctx
            | SetCfg cfg -> setCfg cfg ctx
            | GetCollections channel -> collections channel ctx
            | SetCollections collections -> setCollections collections ctx
            | ActivateAgent index -> activateAgent index ctx
            | ObjectiveCompleted agentCtx -> objectiveCompleted agentCtx ctx
            | ObjectiveFailed (ex, agentCtx) -> objectiveFailed ex agentCtx ctx

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
        let onErr = Observable.subscribe (fun (e: exn) -> failFast (sprintf "unhandled exception occured in director: %s ~ %s" (e.GetType().Name) e.Message) e) agent.Error

        do agent.Start ()

        member __.Terminated = terminated.AsObservable ()

        member __.Collections
            with get () = agent.PostAndReply <| fun channel -> GetCollections channel
            and set value = SetCollections value |> agent.Post

        member __.Cfg
            with get () = agent.PostAndReply <| fun channel -> GetCfg channel
            and set value = SetCfg value |> agent.Post

        member __.Start () =
            let rec start i =
                match i with
                | GTE state.Cfg.MaxWorkers -> ()
                | _ -> ActivateAgent i |> agent.Post; start (i + 1)
            start 0

        interface IDisposable with
            override __.Dispose () =
                if not disposed then
                    disposeSeq [onErr; agent; terminated]
                    disposed <- true