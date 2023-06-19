namespace Venmo.Creator

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
open LibVenmo.Creator
open LibVenmo.Creator.Director

module private Constants =
    let [<Literal>] MaxWorkers = "MaxWorkers"
    let [<Literal>] MaxCreates = "MaxCreates"
    let [<Literal>] DelayBetweenWorkers = "DelayBetweenWorkers"

    let [<Literal>] Proxies = "Proxies"
    let [<Literal>] Devices = "Devices"
    let [<Literal>] FirstNames = "FirstNames"
    let [<Literal>] LastNames = "LastNames"
    let [<Literal>] AreaCodes = "AreaCodes"

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
    | Attempts
    | Created

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
      LblAttempts: Int64Label
      LblCreated: Int64Label
      CancellationToken: CancellationToken }

type UIUpdater (props: UiUpdaterProps, dataGridItems: ObservableCollection<DataGridItem>) =

    let mutable disposed = false
    let mainWindow = props.Window

    let start (agent: MailboxProcessor<UpdateUiMsg>) =

        let handleUpdateStatMsg lbl amt =
            let lblToUpdate =
                match lbl with
                | Attempts -> props.LblAttempts
                | Created -> props.LblCreated
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
    inherit LibVenmo.Creator.UIUpdater ()
    override __.UpdateId value = uiUpdater.UpdateDataGridItem (index, DataGridCol.Account, value)
    override __.UpdateStatus status = uiUpdater.UpdateDataGridItem (index, DataGridCol.Status, status)
    override __.IncrementAttempts () = uiUpdater.UpdateStat (Attempts, 1L)
    override __.IncrementCreated () = uiUpdater.UpdateStat (Created, 1L)

    type MainWindowBase = FsXaml.XAML<"mainwindow.xaml">

type MainWindow () as __ =
    inherit MainWindowBase ()

    let name = Assembly.GetExecutingAssembly().GetName().Name.ToLower()
    let version = Assembly.GetExecutingAssembly().GetName().Version.ToString()

    let lblAttempts = Int64Label ("Attempts", __.LblAttempts)
    let lblCreated = Int64Label ("Created", __.LblCreated)

    let cfg = new Config.Ini (name)

    let createSettingsUI () =
        let maxWorkers =
            SettingDataGridItem (
                Constants.MaxWorkers,
                "Max workers",
                "int",
                SettingValue.Int 1
            )
        let maxCreates =
            SettingDataGridItem (
                Constants.MaxCreates,
                "Max creates",
                "int",
                SettingValue.Int 1
            )
        let delayBetweenWorkers =
            SettingDataGridItem (
                Constants.DelayBetweenWorkers,
                "Delay between worker restarts in seconds",
                "int",
                SettingValue.Int 9
            )

        let proxies =
            SettingDataGridItem (
                Constants.Proxies,
                "Proxies",
                "Sequence",
                SettingValue.Sequence SeqInfo.Default
            )
        let devices =
            SettingDataGridItem (
                Constants.Devices,
                "Devices",
                "Sequence",
                SettingValue.Sequence SeqInfo.Default
            )
        let firstNames =
            SettingDataGridItem (
                Constants.FirstNames,
                "First names",
                "Sequence",
                SettingValue.Sequence SeqInfo.Default
            )
        let lastNames =
            SettingDataGridItem (
                Constants.LastNames,
                "Last names",
                "Sequence",
                SettingValue.Sequence SeqInfo.Default
            )
        let areaCodes =
            SettingDataGridItem (
                Constants.AreaCodes,
                "Area codes",
                "Sequence",
                SettingValue.Sequence SeqInfo.Default
            )

        let items = [ maxWorkers; maxCreates; delayBetweenWorkers; proxies; devices; firstNames; lastNames; areaCodes]
        SettingsDataGrid (items, __.GrdSettingsContent, cfg)

    let settingsUI = createSettingsUI ()

    let onWindowClosing _ =
        cfg.WriteToFile ()
        use proc = Process.GetCurrentProcess ()
        proc.Kill ()

    let queue tryParseFn strings = Seq.chooseValue tryParseFn strings |> Queue.ofSeq

    let createDataGridItems maxWorkers =
        let createDataGridItem _ = DataGridItem (__.WorkerMonitor)
        let sequence = Seq.init maxWorkers createDataGridItem
        let dataGridItems = ObservableCollection (sequence)
        dataGridItems

    let getSeq name =
        settingsUI.GetSeq name

    let cfg () =
        let cfg =
            { MaxWorkers = settingsUI.GetInt (Constants.MaxWorkers)
              MaxCreates = settingsUI.GetInt (Constants.MaxCreates)
              DelayBetweenWorkers = settingsUI.GetInt (Constants.DelayBetweenWorkers)
              PvaProviderType = Console }: Cfg
        cfg.Validate (); cfg

    let collections () =
        let collections =
            { Proxies = getSeq Constants.Proxies |> queue WebProxy.TryParse |> Queue.shuffle
              Devices = getSeq Constants.Devices |> queue AndroidDevice.TryParse |> Queue.shuffle
              FirstNames = getSeq Constants.FirstNames |> Queue.ofSeq |> Queue.shuffle
              LastNames = getSeq Constants.LastNames |> Queue.ofSeq |> Queue.shuffle
              AreaCodes = getSeq Constants.AreaCodes |> Queue.ofSeq |> Queue.shuffle
              CellySeq = ConsoleCellySeq() }
        collections.Validate (); collections

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
        let tryUpdateCollection parseFn update sequence =
            let coll = supervisor.Collections
            let queue = queue parseFn sequence
            match queue with
            | HasItems ->
                let coll: Collections = update coll queue
                try coll.Validate (); supervisor.Collections <- coll
                with
                | :? InvalidOperationException -> ()
            | _ -> ()

        let tryUpdateCfg update value =
            let cfg = supervisor.Cfg
            let cfg: Cfg = update cfg value
            try cfg.Validate (); supervisor.Cfg <- cfg
            with
            | :? InvalidOperationException -> ()

        let onValChanged (name, value: SettingValue) =
            match name with
            | Constants.MaxCreates ->
                let update cfg value = { cfg with MaxCreates = value }
                tryUpdateCfg update value.IntValue
            | Constants.DelayBetweenWorkers ->
                let update cfg value = { cfg with DelayBetweenWorkers = value }
                tryUpdateCfg update value.IntValue
            | Constants.Proxies ->
                let s = value.SequenceValue
                let update coll value = { coll with Proxies = value }
                tryUpdateCollection WebProxy.TryParse update s
            | Constants.Devices ->
                let s = value.SequenceValue
                let update coll value = { coll with Devices = value }
                tryUpdateCollection AndroidDevice.TryParse update s
            | Constants.FirstNames ->
                let s = value.SequenceValue
                let update coll value = { coll with FirstNames = value }
                tryUpdateCollection ValueSome update s
            | Constants.LastNames ->
                let s = value.SequenceValue
                let update coll value = { coll with LastNames = value }
                tryUpdateCollection ValueSome update s
            | Constants.AreaCodes ->
                let s = value.SequenceValue
                let update coll value = { coll with AreaCodes = value }
                tryUpdateCollection ValueSome update s
            | _ -> ()

        settingsUI.ValueChanged.Subscribe (onValChanged)

    let createUiUpdaters uiUpdater max =
        let d = Dictionary<int, LibVenmo.Creator.UIUpdater> ()
        let rec loop index d =
            match index with
            | GTE max -> ReadOnlyDict.ofDict d
            | _ ->
                let uiUpdater = WpfUIUpdater (index, uiUpdater) :> LibVenmo.Creator.UIUpdater
                d.Add (index, uiUpdater)
                loop (index + 1) d
        loop 0 d

    let launchWorkers (cfg: Cfg) (collections: Collections) () =
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
                        LblCreated = lblCreated
                        CancellationToken = token
                    }
                )

            use uiUpdater = new UIUpdater (uiUpdaterProps, uiItems)
            let uiUpdaters = createUiUpdaters uiUpdater cfg.MaxWorkers

            let dProps: Director.Props =
                { UIUpdaters = uiUpdaters
                  CancellationToken = token }

            let dState: Director.State =
                { ActiveWorkers = cfg.MaxWorkers
                  Cfg = cfg
                  Collections = collections
                  Created = 0 }

            use director = new Director (dProps, dState)

            use _ = winTimer uiUpdater
            use _ = cfgValChanged director

            let termination = director.Terminated.ToTask ()
            director.Start ()
            do! Async.AwaitTask termination
            c.Cancel ()

            let updateUiOnWorkCompleted () =
                uiItems.Clear ()
                __.CmdLaunch.IsEnabled <- true
                __.WorkerMonitor.ItemsSource <- []
                MessageBox.Show ("work complete", "work complete", MessageBoxButton.OK, MessageBoxImage.Information) |> ignore
            __.Dispatcher.Invoke updateUiOnWorkCompleted

            return ()
        }

    let validateAndLaunch () =
        async {
            try
                let struct (cfg, collections) = struct (cfg (), collections ())
                return! launchWorkers cfg collections ()
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