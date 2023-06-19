namespace Venmo.CellyVerifier

open System
open System.Collections.Generic
open System.Collections.ObjectModel
open System.Diagnostics
open System.IO
open System.Net
open System.Reactive.Linq
open System.Reflection
open System.Reactive.Threading.Tasks
open System.Threading
open System.Windows
open System.Windows.Controls
open System.Windows.Data

open LibNhk
open LibNhk.Http
open LibNhk.Wpf
open LibNhk.Wpf.ViewModels

open LibVenmo
open LibVenmo.CellyVerifier
open LibVenmo.CellyVerifier.Director

module private Constants =
    let [<Literal>] MaxWorkers = "MaxWorkers"
    let [<Literal>] MinDelayBetweenVerifyAttempts = "MinDelayBetweenVerifyAttempts"
    let [<Literal>] MaxDelayBetweenVerifyAttempts = "MaxDelayBetweenVerifyAttempts"
    let [<Literal>] MinContactsPerVerifySession = "MinContactsPerVerifySession"
    let [<Literal>] MaxContactsPerVerifySession = "MaxContactsPerVerifySession"
    let [<Literal>] MinDelayBetweenVerifySession = "MinDelayBetweenVerifySession"
    let [<Literal>] MaxDelayBetweenVerifySession = "MaxDelayBetweenVerifySession"
    let [<Literal>] MinContactsPerVerifyReq = "MinContactsPerVerifyReq"
    let [<Literal>] MaxContactsPerVerifyReq = "MaxContactsPerVerifyReq"
    let [<Literal>] MaxTotalVerifyAttemptsPerAccount = "MaxTotalVerifyAttemptsPerAccount"
    let [<Literal>] Sessions = "Sessions"
    let [<Literal>] Proxies = "Proxies"
    let [<Literal>] ContactsStream = "ContactsStream"
    let [<Literal>] FirstNames = "FirstNames"

type DataGridItem (dataGrid: DataGrid) =

    inherit ViewModelBase ()

    let mutable account = ""
    let mutable status = ""

    do
        let acctBinding =
            Data.Binding.create "Account"
            |> Data.Binding.setUpdateSourceTrigger UpdateSourceTrigger.PropertyChanged
        let statusBinding =
            Data.Binding.create "Status"
            |> Data.Binding.setUpdateSourceTrigger UpdateSourceTrigger.PropertyChanged
        let acctCol = Seq.item 0 dataGrid.Columns :?> DataGridTextColumn
        let statusCol = Seq.item 1 dataGrid.Columns :?> DataGridTextColumn
        Controls.DataGrid.DataGridColumn.setBinding acctBinding acctCol |> ignore
        Controls.DataGrid.DataGridColumn.setBinding statusBinding statusCol |> ignore

    member this.Account
        with get () = account
        and set (v : string) =
            account <- v
            this.OnPropertyChanged (<@ this.Account @>)

    member this.Status
        with get () = status
        and set (value: string) =
            status <- value
            this.OnPropertyChanged (<@ this.Status @>)

[<Struct>]
type StatLabel =
    | Online
    | Attempts
    | Verified

[<Struct>]
type DataGridCol =
    | Account
    | Status

[<Struct>]
type private UpdateUiMsg =
    | UpdateStatLabel of label: StatLabel * amount: int64
    | UpdateWindowTitle of title: string
    | UpdateDataGridItem of index: int * col: DataGridCol * value: string

type UiUpdaterProps =
    { Window: Window
      LblOnline: Int64Label
      LblAttempts: Int64Label
      LblVerified: Int64Label
      CancellationToken: CancellationToken }

type UIUpdater (props: UiUpdaterProps, dataGridItems: ObservableCollection<DataGridItem>) =

    let mutable disposed = false
    let mainWindow = props.Window

    let start (agent: MailboxProcessor<UpdateUiMsg>) =

        let handleUpdateStatMsg lbl amt =
            let lblToUpdate =
                match lbl with
                | Online -> props.LblOnline
                | Attempts -> props.LblAttempts
                | Verified -> props.LblVerified
            lblToUpdate.Value <- lblToUpdate.Value + amt

        let handleUpdateWinTitleMsg title =
            let fn () = mainWindow.Title <- title
            mainWindow.Dispatcher.Invoke fn

        let handleUpdateDataGridItemMsg index col value =
            match col with
            | Account -> dataGridItems.[index].Account <- value
            | Status -> dataGridItems.[index].Status <- value

        let rec loop () =
            async {
                if not disposed then
                    let! msg = agent.Receive ()
                    match msg with
                    | UpdateStatLabel (lbl, amt) -> handleUpdateStatMsg lbl amt
                    | UpdateWindowTitle title -> handleUpdateWinTitleMsg title
                    | UpdateDataGridItem (index, col, value) -> handleUpdateDataGridItemMsg index col value
                    return! loop ()
            }

        loop ()

    let agent = new MailboxProcessor<UpdateUiMsg> (start, props.CancellationToken)
    do agent.Start ()

    member __.UpdateStat (statLabel: StatLabel, amount: int64) =
        let msg = UpdateUiMsg.UpdateStatLabel (statLabel, amount)
        agent.Post msg

    member __.UpdateWindowTitle (title: string) =
        let msg = UpdateUiMsg.UpdateWindowTitle title
        agent.Post msg

    member __.UpdateDataGridItem (index: int, col: DataGridCol, value: string) =
        let msg = UpdateUiMsg.UpdateDataGridItem (index, col, value)
        agent.Post msg

    interface IDisposable with
        member __.Dispose () =
            if not disposed then
                dispose agent
                disposed <- true

type WpfUIUpdater (index, uiUpdater: UIUpdater) =
    inherit LibVenmo.CellyVerifier.UIUpdater ()
    override __.UpdateId value = uiUpdater.UpdateDataGridItem (index, DataGridCol.Account, value)
    override __.UpdateStatus status = uiUpdater.UpdateDataGridItem (index, DataGridCol.Status, status)
    override __.IncrementAttempts (amt) = uiUpdater.UpdateStat (Attempts, amt)
    override __.IncrementVerified (amt) = uiUpdater.UpdateStat (Verified, amt)
    override __.IncrementOnline () = uiUpdater.UpdateStat (Online, 1L)
    override __.DecrementOnline () = uiUpdater.UpdateStat (Online, -1L)

type MainWindowBase = FsXaml.XAML<"mainwindow.xaml">

type MainWindow () as __ =
    inherit MainWindowBase ()

    let mutable writeWorkerOpt: WriteWorker voption = ValueNone

    let name = Assembly.GetExecutingAssembly().GetName().Name.ToLower()
    let version = Assembly.GetExecutingAssembly().GetName().Version.ToString()

    let lblOnline = Int64Label ("Online", __.LblOnline)
    let lblAttempts = Int64Label ("Attempts", __.LblAttempts)
    let lblVerified = Int64Label ("Verified", __.LblVerified)

    let cfg = new Config.Ini (name)

    let createSettingsUI () =
        let maxWorkers =
            SettingDataGridItem (
                Constants.MaxWorkers,
                "Max workers",
                "int",
                SettingValue.Int 1
            )
        let minAttemptDelay =
            SettingDataGridItem (
                Constants.MinDelayBetweenVerifyAttempts,
                "Min delay in seconds between verify attempts.",
                "int",
                SettingValue.Int 5
            )
        let maxAttemptDelay =
            SettingDataGridItem (
                Constants.MaxDelayBetweenVerifyAttempts,
                "Max delay in seconds between verify attempts.",
                "int",
                SettingValue.Int 10
            )
        let minSessionContacts =
            SettingDataGridItem (
                Constants.MinContactsPerVerifySession,
                "Min amount of contacts to attempt per session",
                "int",
                SettingValue.Int 500
            )
        let maxSessionContacts =
            SettingDataGridItem (
                Constants.MaxContactsPerVerifySession,
                "Max amount of contacts to attempt per session",
                "int",
                SettingValue.Int 1000
            )
        let minSessionDelay =
            SettingDataGridItem (
                Constants.MinDelayBetweenVerifySession,
                "Min delay in minutes between verify sessions of each account.",
                "int",
                SettingValue.Int 5
            )
        let maxSessionDelay =
            SettingDataGridItem (
                Constants.MaxDelayBetweenVerifySession,
                "Max delay in minutes between verify sessions of each account.",
                "int",
                SettingValue.Int 10
            )
        let minContacts =
            SettingDataGridItem (
                Constants.MinContactsPerVerifyReq,
                "Min amount of contacts per verify request.",
                "int",
                SettingValue.Int 20
            )
        let maxContacts =
            SettingDataGridItem (
                Constants.MaxContactsPerVerifyReq,
                "Max amount of contacts per verify request.",
                "int",
                SettingValue.Int 40
            )
        let ttlContacts =
            SettingDataGridItem (
                Constants.MaxTotalVerifyAttemptsPerAccount,
                "Max total amount of verify contact attempts permitted per account.",
                "int",
                SettingValue.Int 9001
            )
        let sessions =
            SettingDataGridItem (
                Constants.Sessions,
                "Sessions/Accounts",
                "Sequence",
                SettingValue.Sequence SeqInfo.Default
            )
        let proxies =
            SettingDataGridItem (
                Constants.Proxies,
                "Proxies",
                "Sequence",
                SettingValue.Sequence SeqInfo.Default
            )
        let contactsStream =
            SettingDataGridItem (
                Constants.ContactsStream,
                "Contacts",
                "Sequence",
                SettingValue.File FileInfo.Default
            )
        let firstNames =
            SettingDataGridItem (
                Constants.FirstNames,
                "First names",
                "Sequence",
                SettingValue.Sequence SeqInfo.Default
            )

        let items =
            [ maxWorkers; minAttemptDelay; maxAttemptDelay; minSessionContacts; maxSessionContacts
              minSessionDelay; maxSessionDelay; minContacts; maxContacts; ttlContacts; sessions;
              proxies; contactsStream; firstNames]
        SettingsDataGrid (items, __.GrdSettingsContent, cfg)

    let settingsUI = createSettingsUI ()

    let queue tryParseFn strings = Seq.chooseValue tryParseFn strings |> Queue.ofSeq

    let createDataGridItems maxWorkers =
        let createDataGridItem _ = DataGridItem (__.WorkerMonitor)
        let sequence = Seq.init maxWorkers createDataGridItem
        ObservableCollection<DataGridItem> (sequence)

    let sequence name = settingsUI.GetSeq name

    let onWindowClosing _ =
        cfg.WriteToFile ()

        match writeWorkerOpt with
        | ValueSome writeWorker -> writeWorker.Write ()
        | ValueNone -> ()

        use proc = Process.GetCurrentProcess ()
        proc.Kill ()

    let cfg () =
        let cfg =
            { MaxWorkers = settingsUI.GetInt (Constants.MaxWorkers)
              MinDelayBetweenVerifyAttempts = settingsUI.GetInt (Constants.MinDelayBetweenVerifyAttempts)
              MaxDelayBetweenVerifyAttempts = settingsUI.GetInt (Constants.MaxDelayBetweenVerifyAttempts)
              MinContactsPerVerifySession = settingsUI.GetInt (Constants.MinContactsPerVerifySession)
              MaxContactsPerVerifySession = settingsUI.GetInt (Constants.MaxContactsPerVerifySession)
              MinDelayBetweenVerifySession = settingsUI.GetInt (Constants.MinDelayBetweenVerifySession)
              MaxDelayBetweenVerifySession = settingsUI.GetInt (Constants.MaxDelayBetweenVerifySession)
              MinContactsPerVerifyReq = settingsUI.GetInt (Constants.MinContactsPerVerifyReq)
              MaxContactsPerVerifyReq = settingsUI.GetInt (Constants.MaxContactsPerVerifyReq)
              MaxTotalVerifyAttemptsPerAccount = settingsUI.GetInt (Constants.MaxTotalVerifyAttemptsPerAccount) }
        cfg.Validate (); cfg

    let tryParseSession input =
        match AuthSession.TryParse (input) with
        | ValueSome s -> Auth s |> ValueSome
        | ValueNone -> ValueNone

    let contacts () =
        let pathToContactsFile = settingsUI.GetFile (Constants.ContactsStream)
        match File.Exists (pathToContactsFile.FileName) with
        | false -> invalidOp "path to contacts file does not exist."
        | true ->
            let fileStream = new FileStream (pathToContactsFile.FileName, FileMode.Open, FileAccess.Read, FileShare.Read)
            let concurrentStreamReader = new ConcurrentStreamReader (fileStream)
            concurrentStreamReader

    let sequences () =
        let sequences: Sequences =
            { Sessions = sequence Constants.Sessions |> queue tryParseSession
              Proxies = sequence Constants.Proxies |> queue WebProxy.TryParse |> Queue.shuffle
              FirstNames = sequence Constants.FirstNames |> Queue.ofSeq |> Queue.shuffle
              ContactsStream = contacts () }
        sequences.Validate (); sequences

    let errMsg (ex: exn) = MessageBox.Show (ex.Message, ex.GetType().Name, MessageBoxButton.OK, MessageBoxImage.Error) |> ignore

    let winTimer (uiUpdater: UIUpdater) =
        let start = DateTimeOffset.Now
        let onTick _ =
            let runTime = DateTimeOffset.Now - start
            let updatedTitle = sprintf "%s %s ~ [%s]" name version (runTime.ToString (@"dd\.hh\:mm\:ss"))
            uiUpdater.UpdateWindowTitle updatedTitle
        let interval = Observable.Interval (TimeSpan.FromMilliseconds 950.0)
        interval.Subscribe (onTick)

    let cfgValChanged (supervisor: Director) =
        let tryUpdateseqsection mkSeqFn parseFn update sequence =
            let seqs = supervisor.Sequences
            let queue = mkSeqFn parseFn sequence
            match queue with
            | HasItems ->
                let seqs: Sequences = update seqs queue
                try seqs.Validate (); supervisor.Sequences <- seqs
                with | :? InvalidOperationException -> ()
            | _ -> ()

        let tryUpdateCfg update value =
            let cfg = supervisor.Cfg
            let cfg: Cfg = update cfg value
            try cfg.Validate (); supervisor.Cfg <- cfg
            with | :? InvalidOperationException -> ()

        let onValChanged (name, value: SettingValue) =
            match name with
            | Constants.MinDelayBetweenVerifyAttempts ->
                let update cfg value = { cfg with MinDelayBetweenVerifyAttempts = value }
                tryUpdateCfg update value.IntValue
            | Constants.MaxDelayBetweenVerifyAttempts ->
                let update cfg value = { cfg with MaxDelayBetweenVerifyAttempts = value }
                tryUpdateCfg update value.IntValue
            | Constants.MinContactsPerVerifySession ->
                let update cfg value = { cfg with MinContactsPerVerifySession = value }
                tryUpdateCfg update value.IntValue
            | Constants.MaxContactsPerVerifySession ->
                let update cfg value = { cfg with MaxContactsPerVerifySession = value }
                tryUpdateCfg update value.IntValue
            | Constants.MinDelayBetweenVerifySession ->
                let update cfg value = { cfg with MinDelayBetweenVerifySession = value }
                tryUpdateCfg update value.IntValue
            | Constants.MaxDelayBetweenVerifySession ->
                let update cfg value = { cfg with MaxDelayBetweenVerifySession = value }
                tryUpdateCfg update value.IntValue
            | Constants.MinContactsPerVerifyReq ->
                let update cfg value = { cfg with MinContactsPerVerifyReq = value }
                tryUpdateCfg update value.IntValue
            | Constants.MaxContactsPerVerifyReq ->
                let update cfg value = { cfg with MaxContactsPerVerifyReq = value }
                tryUpdateCfg update value.IntValue
            | Constants.MaxTotalVerifyAttemptsPerAccount ->
                let update cfg value = { cfg with MaxTotalVerifyAttemptsPerAccount = value }
                tryUpdateCfg update value.IntValue
            | Constants.Proxies ->
                let s = value.SequenceValue
                let update seqs value = { seqs with Proxies = value }
                tryUpdateseqsection queue WebProxy.TryParse update s
            | Constants.FirstNames ->
                let s = value.SequenceValue
                let update seqs value = { seqs with FirstNames = value }
                tryUpdateseqsection queue ValueSome update s
            | Constants.ContactsStream ->
                try
                    let seqs = supervisor.Sequences
                    let seqs = { seqs with ContactsStream = contacts ()}
                    seqs.Validate ()
                    supervisor.Sequences <- seqs
                with | :? InvalidOperationException -> ()
            | _ -> ()

        settingsUI.ValueChanged.Subscribe (onValChanged)

    let createUiUpdaters uiUpdater max =
        let d = Dictionary<int, LibVenmo.CellyVerifier.UIUpdater> ()
        let rec loop index d =
            match index with
            | GTE max -> ReadOnlyDict.ofDict d
            | _ ->
                let uiUpdater = WpfUIUpdater (index, uiUpdater) :> LibVenmo.CellyVerifier.UIUpdater
                d.Add (index, uiUpdater)
                loop (index + 1) d
        loop 0 d

    let launchWorkers (cfg: Cfg) (seqs: Sequences) () =
        async {

            let mkUIItems () =
                let items = createDataGridItems cfg.MaxWorkers
                __.WorkerMonitor.ItemsSource <- items
                items

            let uiItems = mkUIItems |> __.Dispatcher.Invoke

            use c = new CancellationTokenSource ()
            let token = c.Token

            let uiUpdaterProps =
                __.Dispatcher.Invoke (
                    fun () -> {
                        Window = __
                        LblAttempts = lblAttempts
                        LblVerified = lblVerified
                        LblOnline = lblOnline
                        CancellationToken = token
                    }
                )

            use uiUpdater = new UIUpdater (uiUpdaterProps, uiItems)
            let uiUpdaters = createUiUpdaters uiUpdater cfg.MaxWorkers

            use writeWorker = new WriteWorker (FileInfo ("venmo-verified-contacts.txt"))
            writeWorker.Start ()
            writeWorkerOpt <- ValueSome writeWorker

            use blacklist = new Blacklist (BlacklistKind.File "venmo.contacts-blacklist.txt")

            let load () = async { return blacklist.Load () }
            let! load = Async.StartChild (load ())
            do! load

            let dProps: Director.Props =
                { UIUpdaters = uiUpdaters
                  CancellationToken = token
                  WriteWorker = writeWorker
                  ContactsBlacklist = blacklist}

            let dState: Director.State =
                { ActiveWorkers = cfg.MaxWorkers
                  Cfg = cfg
                  Sequences = seqs
                  Agents = Dictionary<string, AgentContainer> () }

            use director = new Director (dProps, dState)

            use _ = winTimer uiUpdater
            use _ = cfgValChanged director

            let termination = director.Terminated.ToTask ()
            director.Activate ()
            do! Async.AwaitTask termination
            c.Cancel ()

            let updateUiOnWorkCompleted () =
                uiItems.Clear ()
                __.CmdLaunch.IsEnabled <- true
                __.WorkerMonitor.ItemsSource <- []
                MessageBox.Show ("work complete", "work complete", MessageBoxButton.OK, MessageBoxImage.Information) |> ignore
            __.Dispatcher.Invoke updateUiOnWorkCompleted

            writeWorkerOpt <- ValueNone
            return ()
        }

    let validateAndLaunch () =
        async {
            try
                let struct (cfg, seqs) = struct (cfg (), sequences ())
                return! launchWorkers cfg seqs ()
            with
            | :? InvalidOperationException as e ->
                __.Dispatcher.Invoke (fun _ ->
                    __.CmdLaunch.IsEnabled <- true
                    __.WorkerMonitor.ItemsSource <- []
                    errMsg e)
                return ()
        }

    let onCmdLaunchClicked _ =
        __.CmdLaunch.IsEnabled <- false
        validateAndLaunch () |> Async.Start

    let onWindowLoaded _ =
        ()

    do
        __.Title <- sprintf "%s %s" name version
        Observable.subscribe onWindowLoaded __.Loaded |> ignore
        Observable.subscribe onWindowClosing __.Closing |> ignore
        Observable.subscribe onCmdLaunchClicked __.CmdLaunch.Click |> ignore