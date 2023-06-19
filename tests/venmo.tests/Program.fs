namespace Venmo.Tests

open System.Collections.Generic
open System.Collections.ObjectModel

open LibVenmo

open Newtonsoft.Json
open Newtonsoft.Json.Linq

module internal Program =

    [<EntryPoint>]
    let main _ =
        let jsonTxt = """{
	"43": {
		"username": "gino59",
		"picture": "https://s3.amazonaws.com/venmo/no-image.gif",
		"is_business": false,
		"name": "Eugenio Gonzalez",
		"firstname": "Eugenio",
		"lastname": "Gonzalez",
		"cancelled": false,
		"date_created": "2017-12-22T20:39:53",
		"external_id": "2375714124857344353",
		"id": "30315312"
	},
	"43": {
		"username": "gino5z9",
		"picture": "https://s3.amazonaws.com/venmo/no-image.gif",
		"is_business": false,
		"name": "Eugenio Gonzalez",
		"firstname": "Eugenio",
		"lastname": "Gonzalez",
		"cancelled": false,
		"date_created": "2017-12-22T20:39:53",
		"external_id": "2375714124857344353",
		"id": "30315312"
	}
}"""
        let jobj = JObject.Parse (jsonTxt)
        let keys = jobj.Properties ()
        0