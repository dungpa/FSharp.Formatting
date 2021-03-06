﻿namespace FSharp.Literate

open FSharp.Markdown
open FSharp.Patterns
open FSharp.CodeFormat
open System.Collections.Generic

// --------------------------------------------------------------------------------------
// CodeBlockUtils module
// --------------------------------------------------------------------------------------

/// Parsing of F# Script files with Markdown commands. Given a parsed script file, we 
/// split it into a sequence of comments, snippets and commands (comment starts with 
/// `(**` and ending with `*)` are translated to Markdown, snippet is all other F# code 
/// and command looks like `(*** key1:value, key2:value ***)` (and should be single line).
module internal CodeBlockUtils =
  type Block = 
    | BlockComment of string
    | BlockSnippet of Line list 
    | BlockCommand of IDictionary<string, string>

  /// Trim blank lines from both ends of a lines list & reverse it (we accumulate 
  /// lines & we want to remove all blanks before returning BlockSnippet)
  let private trimBlanksAndReverse lines = 
    lines 
    |> Seq.skipWhile (function Line[] -> true | _ -> false)
    |> List.ofSeq |> List.rev
    |> Seq.skipWhile (function Line[] -> true | _ -> false)
    |> List.ofSeq

  /// Succeeds when a line (list of tokens) contains only Comment 
  /// tokens and returns the text from the comment as a string
  /// (Comment may also be followed by Whitespace that is skipped)
  let private (|ConcatenatedComments|_|) (Line tokens) =
    let rec readComments inWhite acc = function
      | Token(TokenKind.Comment, text, _)::tokens when not inWhite-> 
          readComments false (text::acc) tokens
      | Token(TokenKind.Default, String.WhiteSpace _, _)::tokens ->
          readComments true acc tokens
      | [] -> Some(String.concat "" (List.rev acc))
      | _ -> None
    readComments false [] tokens

  // Process lines of an F# script file. Simple state machine with two states
  //  * collectComment - we're parsing a comment and waiting for the end
  //  * collectSnippet - we're in a normal F# code and we're waiting for a comment
  //    (in both states, we also need to recognize (*** commands ***)

  /// Waiting for the end of a comment      
  let rec private collectComment (comment:string) lines = seq {
    match lines with
    | (ConcatenatedComments(String.StartsAndEndsWith ("(***", "***)") (ParseCommands cmds)))::lines ->
        // Ended with a command, yield comment, command & parse the next as a snippet
        let cend = comment.LastIndexOf("*)")
        yield BlockComment (comment.Substring(0, cend))
        yield BlockCommand cmds
        yield! collectSnippet [] lines

    | (ConcatenatedComments text)::_ when 
        comment.LastIndexOf("*)") <> -1 && text.Trim().StartsWith("//") ->
        // Comment ended, but we found a code snippet starting with // comment
        let cend = comment.LastIndexOf("*)")
        yield BlockComment (comment.Substring(0, cend))
        yield! collectSnippet [] lines

    | (Line[Token(TokenKind.Comment, String.StartsWith "(**" text, _)])::lines ->
        // Another block of Markdown comment starting... 
        // Yield the previous snippet block and continue parsing more comments
        let cend = comment.LastIndexOf("*)")
        yield BlockComment (comment.Substring(0, cend))
        if lines <> [] then yield! collectComment text lines

    | (ConcatenatedComments text)::lines  ->
        // Continue parsing comment
        yield! collectComment (comment + "\n" + text) lines

    | lines ->
        // Ended - yield comment & continue parsing snippet
        let cend = comment.LastIndexOf("*)")
        yield BlockComment (comment.Substring(0, cend))
        if lines <> [] then yield! collectSnippet [] lines }

  /// Collecting a block of F# snippet
  and private collectSnippet acc lines = seq {
    match lines with 
    | (ConcatenatedComments(String.StartsAndEndsWith ("(***", "***)") (ParseCommands cmds)))::lines ->
        // Found a special command, yield snippet, command and parse another snippet
        if acc <> [] then yield BlockSnippet (trimBlanksAndReverse acc)
        yield BlockCommand cmds
        yield! collectSnippet [] lines

    | (Line[Token(TokenKind.Comment, String.StartsWith "(**" text, _)])::lines ->
        // Found a comment - yield snippet & switch to parsing comment state
        if acc <> [] then yield BlockSnippet (trimBlanksAndReverse acc)
        yield! collectComment text lines

    | x::xs ->  yield! collectSnippet (x::acc) xs
    | [] -> yield BlockSnippet (trimBlanksAndReverse acc) }

  /// Parse F# script file into a sequence of snippets, comments and commands
  let parseScriptFile = collectSnippet []

// --------------------------------------------------------------------------------------
// LiterateScript module
// --------------------------------------------------------------------------------------

/// Turns the content of `fsx` file into `LiterateDocument` that contains
/// formatted F# snippets and parsed Markdown document. Handles commands such
/// as `hide`, `define` and `include`.
module internal ParseScript = 
  open CodeBlockUtils

  /// Transform list of code blocks (snippet/comment/command)
  /// into a formatted Markdown document, with link definitions
  let rec private transformBlocks acc defs blocks = 
    match blocks with
    // Reference to code snippet defined later
    | BlockCommand(Command "include" ref)::blocks -> 
        let p = EmbedParagraphs(CodeReference(ref))
        transformBlocks (p::acc) defs blocks
    // Hidden code block or hidden definition with 'ref' reference code
    | Let "" (ref, BlockCommand(Command "hide" _)::BlockSnippet(snip)::blocks) 
    | BlockCommand(Command "define" ref)::BlockSnippet(snip)::blocks ->
        let acc = 
          if ref = "" then acc
          else (EmbedParagraphs(HiddenCode(Some ref, snip)))::acc
        transformBlocks acc defs blocks
    // Unknown command
    | BlockCommand(cmds)::_ ->
        failwith "Unknown commands: %A" [for (KeyValue(k, v)) in cmds -> sprintf "%s:%s" k v]

    // Skip snippets with no content
    | BlockSnippet([])::blocks ->
        transformBlocks acc defs blocks
    // Ordinary F# code snippet
    | BlockSnippet(snip)::blocks ->
        let p = EmbedParagraphs(FormattedCode(snip))
        transformBlocks (p::acc) defs blocks
    // Markdown documentation block  
    | BlockComment(text)::blocks ->
        let doc = Markdown.Parse(text)
        let defs = doc.DefinedLinks::defs
        let acc = (List.rev doc.Paragraphs) @ acc
        transformBlocks acc defs blocks
    | [] -> 
        // Union all link definitions & return Markdown doc
        let allDefs = 
          [ for def in defs do for (KeyValue(k, v)) in def -> k, v ] |> dict 
        List.rev acc, allDefs

  /// Parse script file with specified name and content
  /// and return `LiterateDocument` with the content
  let parseScriptFile file content (ctx:CompilerContext) =
    let sourceSnippets, errors = 
      ctx.FormatAgent.ParseSource
        (file, content, ?options = ctx.CompilerOptions, ?defines = ctx.DefinedSymbols)
    let (Snippet(_, lines)) = match sourceSnippets with [| it |] -> it | _ -> failwith "multiple snippets"
    let parsedBlocks = parseScriptFile lines 
    let paragraphs, defs = transformBlocks [] [] (List.ofSeq parsedBlocks)
    LiterateDocument(paragraphs, "", defs, LiterateSource.Script sourceSnippets, errors)