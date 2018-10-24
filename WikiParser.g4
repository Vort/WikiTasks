parser grammar WikiParser;

options { tokenVocab = WikiLexer; }

init : (wikiword | pipe | RSB)* EOF ;

url : URL ;
words : WORD+;
ilink : ILINKSTART wikiword* (pipe wikiword*)? ILINKEND ;
flink : FLINKSTART words param* ILINKEND ;
elink : LSB url (wikiword | pipe | LSB)* RSB ;
pipe : PIPE | NLPIPE ;

param : pipe (words EQ)? (wikiword | RSB)* ;
templ : TEMPLSTART words param* TEMPLEND ;
magic : TEMPLSTART words COLON wikiword* (pipe wikiword*)* TEMPLEND ;
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
         | COLON
         | templ
         | magic
         | table
         ;