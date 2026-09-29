CSS parser cases, written from https://www.w3.org/TR/css-syntax-3/#parsing and
https://drafts.csswg.org/css-nesting-1/.

Format: "=== name", the input (\uXXXX escapes), "---", then the rules:
  style <prelude>              a style rule, its declarations and child rules indented below
  at <name> <prelude> [{]      an at-rule; "{" when it has a block
  declarations                 declarations that follow a nested rule
  <name>: <value> [!important]
Preludes and values are shown with whitespace collapsed.

The tokenizer cases in ../Tokenizer use one token per line.
