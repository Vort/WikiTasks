lexer grammar WikiLexer;

TEMPLSTART : '{{' ;
TEMPLEND : '}}' | (WS* '|}}') ;
FLINKSTART : '[['[\u0424\u0444]'\u0430\u0439\u043B:'
           | '[['[\u0418\u0438]'\u0437\u043E\u0431\u0440\u0430\u0436\u0435\u043D\u0438\u0435:'
           | '[['[Ff]'ile:'
           | '[['[Ii]'mage:' ;
ILINKSTART : '[[' ;
ILINKEND : ']]' ;
LSB : '[' ;
RSB : ']' ;
COLON : ':';
TABLESTART : NLWS LCB PIPE ;
TABLEEND : NLWS PIPE RCB ;
TABLEROWSEP : NLWS '|-' ;
PIPE : '|';
NLPIPE : NLWS PIPE ;
HEADING : '==' '='* ;
EQ : '=' | '{{=}}';
LCB : '{' ;
RCB : '}' ;
TAG : '<' '/'? [a-zA-Z] .*? '/'? '>' ;
LTGT : '<' | '>' ;
SKIPPED : ('<pre>' .*? '</pre>')
        | ('<code>' .*? '</code>')
        | ('<math>' .*? '</math>')
        | ('<source' .*? '</source>')
        | ('<gallery' .*? '</gallery>')
        | ('<nowiki>' .*? '</nowiki>')
        | ('<!--' .*? '-->') ;
URL : ('ftp:'|'http:'|'https:')? '//' [0-9a-zA-Z_.~-]+ ~([ \r\n\t]|[<|}]|']')+ ;
WORD : WS+ | ~([ \r\n\t]|[:{}<>|=]|'['|']')+ ;
fragment WS : NL | ST ;
fragment NL : '\r'? '\n' ;
fragment ST : ' ' | '\t' ;
fragment NLWS : ST* (NL ST*)+ ;