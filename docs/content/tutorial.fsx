(*** hide ***)
// This block of code is omitted in the generated HTML documentation. Use 
// it to define helpers that you do not want to show in the documentation.
#I "../../bin"
#r "TransAlt/TransAlt.dll"
open TransAlt
open Alt
open Channel
open Lens
open System.Threading
open State

(**
Getting started 
========================

This is a simple library which allows you to compose your async computations into transactions.
main combinators are:

* fromAsync(async) will wrap any async workflow into an Alt object
* pick(state,alt) will run your Alt object with specified state
* withAck(ackAlt -> alt) allows you to create your Alt object with attached handlers for success/failed commit
* merge(alt1,alt2) will return tuple with results of alts executed in parallel
* choose(alt1,alt2) will return commit result only from first non error subcommit, other sub commit will be declined
* bind(alt<'a>,'a -> Alt<'b>) allow to sequentially compose your alt computations

# How it works
The main type is Alt, it defines some async computation which should commit result or error after execution

*)
module Alt_ =
    type Alt_<'s,'r when 's : not struct> = 
            Alt of (ProcessId * Transaction<'s,'r> ->  Async<unit>) * IsMutatesState

    let fromAsync_ wrkfl =
            Alt((fun (_,tran:Transaction<'s,'r>) ->
                async{
                    try
                        let! res = wrkfl
                        let! _ = tran.commit (Ok(res))
                        return ()
                    with error -> let! _ = tran.commit (Error(error))
                                  return ()
                }),false
            )
(**
But commit could be unsuccessful so you could add some handlers for successful/unsuccessful commit.
*)
    let withAck_ (builder:Alt<'s, bool> -> Async<Alt<'s,'r>>) =  
            Alt((fun (procId,tran) ->
                async{
                    let nack = Promise.create<bool>()
                    let commit res = async{
                        let! commited = tran.commit res
                        nack.signal(commited) |> ignore
                        return commited}
                    let tran = {commit = commit; state = tran.state}
                    let! alt = builder(fromAsync(nack.future))
                    run procId tran alt 
                }), true)
    let altWithNack = withAck (fun (ack : Alt<unit, bool>) -> 
                                let ack = map(ack, fun x -> if x then printfn "do commit"
                                                            else printfn "do rollback"
                                                            ())
                                asyncReturn <| choose(Async.Sleep(100) |> fromAsync,ack)
                                )
(**
#State and communication
If your workflows have to communicate or share some state then you need to create some immutable object which will keep all shared data.
Currently implemented only support for immutable channels, but it is easy to add your own.
Lets define a channel and communicate two workflows. Your workflows should not use state mutation like in state monad but delegate change operations to a state keeper.
So you have to define lenses which dramatically simplify state changes.
*)
let St : Channel<int> = EmptyBounded(1) "channel"
let id_lens = { get = fun r -> r; 
                set = fun (r,v) -> v}
Alt.merge(id_lens.enq 1, id_lens.deq ()) |> pickWithResultState St |> Async.RunSynchronously |> printfn "%A"

(**
Result is (Ok (null, 1), queue(channel []))

Why immutability? Because it is simple to share state between concurrent threads started by choose combinator and isolate changes from each other.
 And internally state keeper is able to resolve blocking problems and stop execution when workflows are deadlocked.
#Builders
Library defines several builder which will help you to compose complex computations. 

* transB monadic builder
* mergeB syntactic sugar for merge function
* chooseB syntactic sugar for choose function
* queryB inspired by joinads match

#Samples
from [joinads sample](https://github.com/tpetricek/FSharp.Joinads/blob/master/README.markdown)
*)

type St2 =
    { putStringC: Channel<string>; 
      putIntC: Channel<int>; 
      echoC: Channel<string>}

    static member putString =
        { get = fun r -> r.putStringC; 
          set = fun (r,v) -> { r with putStringC = v }}

    static member putInt =
        { get = fun r -> r.putIntC; 
          set = fun (r,v) -> { r with putIntC = v }}

    static member echo =
        { get = fun r -> r.echoC; 
          set = fun (r,v) -> { r with echoC = v }}

let state = {putStringC = EmptyUnbounded "putStringC"
             putIntC = EmptyUnbounded "putIntC"
             echoC = EmptyUnbounded "echoC"} 

let rec whileOk alt = tranB{
                         do! alt 
                         return! whileOk alt
                      } 

let getPutString = tranB{
    let! v = St2.putString.deq()
    do! St2.echo.enq(sprintf "Echo %s" v)
}

let getPutInt = tranB{
    let! v = St2.putInt.deq()
    do! St2.echo.enq(sprintf "Echo %d" v)
}

let getPut = choose(getPutString, getPutInt)

let getEcho = tranB{
    let! s = St2.echo.deq()
    Logger.logf "getEcho" "GOT: %A" s
}
let put5 =tranB { 
            for i in [1 .. 5] do
                Logger.logf "put5" "iter %d" i
                do! St2.putString.enq(sprintf "Hello %d!" i) 
                do! St2.putInt.enq(i)} 
mergeB{
    case put5
    case (whileOk getPut)
    case (whileOk getEcho)
} |> pickWithResultState state |> Async.RunSynchronously |> printfn "%A"

(**
async cancellation from [hopac samples](https://github.com/Hopac/Hopac/blob/master/Docs/Alternatives.md)
*)
let asyncWitchCancellation wrkfl =
    withAck(fun nack -> async{
        let cts = new CancellationTokenSource()
        let wrkfl, res = Promise.wrapWrkfl(wrkfl)
        Async.Start(wrkfl, cts.Token)
        let nack = map(nack, fun commited ->  
                                    if not commited then printfn "async cancelled"
                                                         cts.Cancel())
        async{
            let! _ = pick () nack
            return () 
        } |> Async.Start
        return fromAsync res
    })
let wrkfl = async{
    do! Async.Sleep(1000)
    return "async finished"
}
(asyncWitchCancellation wrkfl, always "always finished") |> choose |> pick () |> Async.RunSynchronously |> printfn "%A"
(asyncWitchCancellation wrkfl, never()) |> choose |> pick () |> Async.RunSynchronously |> printfn "%A"

(**
fetcher from [hopac docs](https://github.com/Hopac/Hopac/blob/master/Docs/Alternatives.md)
*)
open Microsoft.FSharp.Control.WebExtensions
open System.Net
open System

let fetchAsync (name, url:string) = async { 
  let uri = new System.Uri(url)
  let webClient = new WebClient()
  let! html = webClient.AsyncDownloadString(uri)
  return sprintf "Read %d characters for %s" html.Length name
}

let fetchAlt (name, url) : Alt<'s,string> =
  fetchAsync (name, url) |> asyncWitchCancellation

let urlList = [ "Microsoft.com", "http://www.microsoft.com/" 
                "MSDN", "http://msdn.microsoft.com/" 
                "Bing", "http://www.bing.com" ]

let runFastest () =
  urlList
  |> Seq.map fetchAlt
  |> chooseXs
  |> pick ()
  |> Async.RunSynchronously

let runAll () =
  urlList
  |> Seq.map fetchAlt
  |> mergeXs
  |> pick ()
  |> Async.RunSynchronously

runFastest() |> printfn "%A"
runAll() |> printfn "%A"

(**
one place buffer from [joinads sample](https://github.com/tpetricek/FSharp.Joinads/blob/master/src/Joins/Samples.fs)
*)
type St3 =
    { putC: Channel<string>; 
      getC: Channel<string>; 
      emptyC: Channel<unit>; 
      containsC: Channel<string>}

    static member put =
        { get = fun r -> r.putC; 
          set = fun (r,v) -> { r with putC = v }}

    static member get =
        { get = fun r -> r.getC; 
          set = fun (r,v) -> { r with getC = v }}

    static member empty =
        { get = fun r -> r.emptyC; 
          set = fun (r,v) -> { r with emptyC = v }}

    static member contains =
        { get = fun r -> r.containsC; 
          set = fun (r,v) -> { r with containsC = v }}

let stateSt3 = { putC = EmptyUnbounded "putC"
                 getC = EmptyUnbounded "getC"
                 emptyC = EmptyUnbounded "emptyC"
                 containsC = EmptyUnbounded "containsC"}
let add_empty = St3.empty.enq ()
let alts = chooseB{
    case (tranB{
        do! St3.empty.deq()
        let! x = St3.put.deq()
        do! St3.contains.enq(x) 
    })
    case (tranB{
        let! v = St3.contains.deq()
        do! St3.get.enq(v) 
        do! St3.empty.enq()
    })} 

let put = tranB { 
        do! fromAsync <| Async.Sleep 1000
        for i in 0 .. 10 do
          Logger.logf "put" "putting: %d" i
          do! St3.put.enq(string i) 
          do! fromAsync <| Async.Sleep 500 }

let got = tranB { 
            do! fromAsync <| Async.Sleep 250
            let! v = St3.get.deq()
            Logger.logf "got" "got: %s" v 
        }
mergeXs [whileOk got; put; whileOk alts; add_empty] |> pick stateSt3 |> Async.RunSynchronously |> printfn "%A"
(**
Dinning philosophers from [joinads sample](http://tryjoinads.org/docs/examples/philosophers.html)
*)
let n = 5
let mapReplace k v map =
    let r = Map.remove k map
    Map.add k v r

type St4 =
    { chopsticksCs: Map<int,Channel<unit>>; 
      hungryC: Map<int,Channel<unit>>;}

    static member chopsticks i =
        { get = fun r -> Logger.logf "philosophers" "getting chopsticksCs %d " i
                         r.chopsticksCs.[i]; 
          set = fun (r,v) -> {r with chopsticksCs = mapReplace i v r.chopsticksCs}}
                             
    static member hungry i =
        { get = fun r -> Logger.logf "philosophers" "getting hungry %d " i
                         r.hungryC.[i]; 
          set = fun (r,v) -> {r with hungryC = mapReplace i v r.hungryC}}

let phioSt = {chopsticksCs = [ for i = 1 to n do yield i, EmptyUnbounded("chopsticksCs")] |> Map.ofList
              hungryC = [ for i = 1 to n do yield i, EmptyBounded 1 "hungryC" ] |> Map.ofList}

let philosophers = [| "Plato"; "Konfuzius"; "Socrates"; "Voltaire"; "Descartes" |]

let randomDelay (r : Random) = Async.Sleep(r.Next(1, 3) * 1000) |> fromAsync

let queries = Array.ofSeq (seq{
                            for i = 1 to n do
                                Logger.logf "philosophers" "left %d " i
                                let left = St4.chopsticks i
                                Logger.logf "philosophers" "left %d "(i % n + 1)
                                let right = St4.chopsticks (i % n + 1)
                                let random = new Random()
                                yield queryB{
                                    for _,_,_ in ((St4.hungry i).deq(), left.deq(), right.deq()) do
                                    select(i,random,left,right)
                                }
                          }) 
let findAndDo = tranB{
                    let! i,random,left,right = chooseXs(queries)
                    Logger.logf "philosophers" "%d wins " i
                    Logger.logf "philosophers" "%s is eating" philosophers.[i-1] 
                    do! randomDelay random
                    do! left.enq()  
                    do! right.enq()  
                    Logger.logf "philosophers" "%s is thinking" philosophers.[i-1] 
                    return ()
                }
    
let add_chopsticks = tranB{
    for i in 1..n do
        do! (St4.chopsticks i).enq()
    }
let random = new Random()  
let hungrySet = tranB{  
        let i = random.Next(1, n)
        Logger.logf "philosophers" "set hungry %s"  philosophers.[i]
        do! (St4.hungry i).enq()
        do! randomDelay random
}

mergeXs [whileOk findAndDo;whileOk hungrySet;add_chopsticks] |> pickWithResultState phioSt |> Async.RunSynchronously |> printfn "%A"

(**
More examples in [tests](https://github.com/hodzanassredin/TransAlt/blob/master/tests/TransAlt.Tests/Tests.fs)
*)