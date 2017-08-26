parser grammar WikiParser;

options { tokenVocab = WikiLexer; }

init : (wikiword | pipe | RSB)* EOF ;

url : URL ;
ilink : ILINKSTART wikiword* (pipe wikiword*)? ILINKEND ;
flink : FLINKSTART WORD+ param* ILINKEND ;
elink : LSB url (wikiword | pipe | LSB)* RSB ;
pipe : PIPE | NLPIPE ;

param : pipe (WORD+ EQ)? (wikiword | RSB)* ;
templ : TEMPLSTART WORD+ param* TEMPLEND ;
table : TABLESTART tablerow (tablerowsep tablerow)* TABLEEND ;
tablerowsep : TABLEROWSEP | NLPIPE | PIPE PIPE ;
tablerow : (wikiword | PIPE | RSB)* ;

wikiword : EQ
         | SKIPPED
         | TAG
         | LTGT
         | HEADING
         | WORD
         | LCB
		 | RCB
         | url
         | ilink
         | flink
         | elink
		 | LSB
         | templ
         | table
         ;