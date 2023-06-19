namespace Venmo.Creator

open System
open System.Windows

module internal Program =

    [<EntryPoint; STAThread>]
    let main _ =
        let app = Application ()
        let win = MainWindow ()
        app.Run (win)