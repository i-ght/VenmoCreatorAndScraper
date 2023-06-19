module LibVenmo

open System
open System.Collections.Generic
open System.IO
open System.IO.Compression
open System.Linq
open System.Text
open System.Net
open System.Reflection

open LibNhk.Http
open LibNhk

open Newtonsoft.Json
open Newtonsoft.Json.Serialization

module internal Constants =
    let [<Literal>] Version = "7.31.1" //"7.34.0"
    let [<Literal>] VersionCode = "1226" //"1322"

type AndroidDevice =
    { Manufactuer: string
      Model: string
      Version: string }
with
    override __.ToString () = sprintf "%s|%s|%s" __.Manufactuer __.Model __.Version
    static member TryParse (input: string) =
        let tryParse () =
            let sp = input.Split ('|')
            match sp with
            | HasCntItems 3 & AllNotEmptyOrWhiteSpace -> { Manufactuer=sp.[0]; Model=sp.[1]; Version=sp.[2] } |> ValueSome
            | _ -> ValueNone
        match input with
        | IsNullOrWhiteSpace -> ValueNone
        | _ -> tryParse ()

type AnonSession =
    { Device: AndroidDevice
      Proxy: WebProxy voption
      DeviceId: string }

type AuthSession =
    { AccessToken: string
      UserId: string
      DeviceId: string
      Proxy: WebProxy voption
      Email: string
      Password: string
      PhoneNumber: string
      Device: AndroidDevice}
with
    override __.ToString () = sprintf "%s:%s:%s:%s:%s:%s:%O" __.AccessToken __.UserId __.DeviceId __.Email __.Password __.PhoneNumber __.Device

    static member TryParse (input: string) =

        let tryParse () =
            let sp = input.Split (':')
            match sp with
            | HasCntItems 7 & AllNotEmptyOrWhiteSpace ->
                match AndroidDevice.TryParse (sp.[6]) with
                | ValueNone -> ValueNone
                | ValueSome device ->
                    { AccessToken = sp.[0]
                      UserId = sp.[1]
                      DeviceId = sp.[2]
                      Proxy = ValueNone
                      Email = sp.[3]
                      Password = sp.[4]
                      PhoneNumber = sp.[5]
                      Device = device } |> ValueSome
            | _ -> ValueNone

        match input with
        | IsNullOrWhiteSpace -> ValueNone
        | _ -> tryParse ()

type Session =
    | Anon of anon: AnonSession
    | Auth of auth: AuthSession
with
    member __.AuthVal =
        match __ with
        | Auth a -> a
        | Anon _ -> invalidOp "session not auth."

    member __.AnonVal =
        match __ with
        | Auth _ -> invalidOp "session non anon."
        | Anon a -> a

    member __.Device =
        match __ with
        | Anon a -> a.Device
        | Auth a -> a.Device

    member __.DeviceId =
        match __ with
        | Anon a -> a.DeviceId
        | Auth a -> a.DeviceId

    member __.Proxy =
        match __ with
        | Anon a -> a.Proxy
        | Auth a -> a.Proxy

type RegisterInfo =
    { FirstName: string
      LastName: string
      Phone: string
      ClientId: int
      Password: string
      Email: string }

type Contact =
    { [<JsonProperty ("i")>]
      Index: string
      [<JsonProperty ("e")>]
      Emails: string list
      [<JsonProperty ("n")>]
      Name: string
      [<JsonProperty ("p")>]
      CellyNumbers: string list }

type UserInfo =
    { Username: string
      [<JsonProperty ("is_business")>]
      IsBusiness: bool
      [<JsonProperty("display_name")>]
      Name: string
      [<JsonProperty("first_name")>]
      FirstName: string
      [<JsonProperty("last_name")>]
      LastName: string
      [<JsonProperty ("date_joined")>]
      DateJoined: DateTimeOffset
      [<JsonProperty ("external_id")>]
      ExternalId: string
      Id: string
      Email: string }
with
    member __.ToCsvRow () = sprintf "\"%s\",\"%b\",\"%s\",\"%s\",\"%s\",\"%O\",\"%s\",\"%s\"" __.Username __.IsBusiness __.Name __.FirstName __.LastName __.DateJoined __.ExternalId __.Id

type UserInfoWithNumber =
    { PhoneNumber: string
      Username: string
      [<JsonProperty ("is_business")>]
      IsBusiness: bool
      Name: string
      FirstName: string
      LastName: string
      [<JsonProperty ("date_created")>]
      DateCreated: DateTimeOffset
      [<JsonProperty ("external_id")>]
      ExternalId: string
      Id: string }
    with
        member __.ToCsvRow () = sprintf "\"%s\",\"%s\",\"%b\",\"%s\",\"%s\",\"%s\",\"%O\",\"%s\",\"%s\"" __.PhoneNumber __.Username __.IsBusiness __.Name __.FirstName __.LastName __.DateCreated __.ExternalId __.Id

        static member OfUserInfo (phoneNumber,userInfo: UserInfo) =
            { PhoneNumber = phoneNumber
              Username = userInfo.Username
              IsBusiness = userInfo.IsBusiness
              Name = userInfo.Name
              FirstName = userInfo.FirstName
              LastName = userInfo.LastName
              DateCreated = userInfo.DateJoined
              ExternalId = userInfo.ExternalId
              Id = userInfo.Id }

type RegUser =
    { Id: string }

type RegisterResp =
    { [<JsonProperty ("access_token")>]
      AccessToken: string
      [<JsonProperty ("user")>]
      User: RegUser }

type RegRespContainer =
    { [<JsonProperty("data")>]
      Data: RegisterResp }

type ApiErr =
    { Message: string
      Code: int
      Title: string }

type ApiErrResp =
    { Error: ApiErr }

type SessionNotAuthorizedException (msg: string) =
    inherit InvalidOperationException (msg)

module Json =
    let serialize o = JsonConvert.SerializeObject (o)
    let deserialize<'a> o = JsonConvert.DeserializeObject<'a> (o)

module ApiClient =

    let private headers (session: Session) =
        let headers =
            [ struct ("User-Agent", sprintf "Venmo/%s Android/%s %s/%s" Constants.Version session.Device.Version session.Device.Manufactuer session.Device.Model)
              struct ("device-id", session.DeviceId)
              struct ("X-Venmo-Android-Version-Name", Constants.Version)
              struct ("X-Venmo-Android-Version-CODE", Constants.VersionCode)
              struct ("application-id", "com.venmo") ]
        match session with
        | Auth a -> struct ("Authorization", sprintf "bearer %s" a.AccessToken) :: headers
        | Anon _ -> headers

    let private req (method: HttpMethod) (uri: string) headers (session: Session) =
        { Request.Create (method, uri) with
            Proxy = session.Proxy
            AcceptEncoding = DecompressionMethods.GZip
            Headers = headers }

    let private postReq (uri: string) (session: Session) body =
        let headers = struct ("Content-Type", "application/x-www-form-urlencoded") :: headers session
        { req POST uri headers session with ContentBody = ContentBody.FormValues body }

    let register (info: RegisterInfo) (session: Session) =
        async {
            let body = [
                struct ("last_name", info.LastName)
                struct ("first_name", info.FirstName)
                struct ("phone_number", info.Phone)
                struct ("email", info.Email)
                struct ("password", info.Password)
                struct ("client_id", info.ClientId |> string)
            ]
            let req = postReq "https://api.venmo.com/v1/users" session body
            let! resp = Http.AsyncRetrieveResponse (req)
            return resp
        }

    let syncContacts (contacts: Contact seq) (session: Session) =
        async {
            let json = Json.serialize {|contacts=contacts|}
            use output = new MemoryStream (json.Length)
            let input = Encoding.UTF8.GetBytes (json)
            use gzip = new GZipStream (output, CompressionMode.Compress)
            gzip.Write (input, 0, input.Length)
            gzip.Dispose ()
            let gzipped = output.ToArray ()
            gzipped.[8] <- 4uy
            //let base64Contacts = Convert.ToBase64String (gzipped)
            //let content =
            //    [ struct ("contacts_gzip", "1")
            //      struct ("contacts", base64Contacts) ]

            let accessToken = session |> function | Auth a -> a.AccessToken | _ -> invalidOp "session not authenticated."
            //let req = postReq "https://venmo.com/api/v5/contacts" session content
            let headers =
                [ struct ("Content-Type", "application/json; charset=UTF-8") 
                  struct ("Content-Encoding", "gzip")
                  struct ("Authorization", sprintf "bearer %s" accessToken) 
                  struct ("User-Agent", "okhttp/3.12.0") ]
            let req = //postReq "https://venmo.com/api/v1/contacts" session content
                { req POST "https://api.venmo.com/v1/contacts" headers session with ContentBody = ByteArray gzipped }
            let! resp = Http.AsyncRetrieveResponse (req)
            return resp
        }

    let me (session: Session) =
        async {
            let req = req GET "https://api.venmo.com/v1/account" (headers session) session
            let! resp = Http.AsyncRetrieveResponse (req)
            return resp
        }

    let updateNumber (phone: string) (session: Session) = async {
        let headers = struct ("Content-Type","application/x-www-form-urlencoded") :: headers session
        let content =
            [ struct("phone", phone) ]
        let req =
            { req POST "https://api.venmo.com/v1/account/phones" headers session with 
                ContentBody = ContentBody.FormValues content }
        let! resp = Http.AsyncRetrieveResponse(req)
        return resp
    }

    let validateCode (phone: string) (code: string) (session: Session) = async {
        let headers = struct ("Content-Type","application/x-www-form-urlencoded") :: headers session
        let content =
            [ struct("phone", sprintf "+%s" phone) 
              struct("code", code) ]
        let req =
            { req PUT "https://api.venmo.com/v1/account/phones" headers session with 
                ContentBody = ContentBody.FormValues content }
        let! resp = Http.AsyncRetrieveResponse(req)
        return resp
    }