Tokenizer cases, written from https://html.spec.whatwg.org/multipage/parsing.html#tokenization.

Format:
  === case name
  @state <TokenizerState> <last start tag>   (optional: initial state, e.g. RcData title)
  @cdata                                     (optional: CDATA sections allowed)
  input lines, with \uXXXX escapes
  ---
  one line per token, then one "! code" line per parse error, in order

Tokens: <!DOCTYPE name public="..." system="..." quirks>, <tag attr="value">, <tag />, </tag>,
<!--comment-->, and "characters" (consecutive runs merged; \n, \t, \", \\ and \uXXXX escapes).
