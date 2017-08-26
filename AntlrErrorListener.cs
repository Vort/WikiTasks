using Antlr4.Runtime;
using System.Collections.Generic;

namespace WikiTasks
{
    class AntlrErrorListener : IAntlrErrorListener<IToken>, IAntlrErrorListener<int>
    {
        public int LexerErrors;
        public int ParserErrors;
        public List<string> ErrorList;

        public AntlrErrorListener()
        {
            ErrorList = new List<string>();
        }

        public void SyntaxError(IRecognizer recognizer, int offendingSymbol,
            int line, int charPositionInLine, string msg, RecognitionException e)
        {
            LexerErrors++;
            ErrorList.Add(" Строка " + line + ":" + charPositionInLine + " [L] " + msg);
        }

        public void SyntaxError(IRecognizer recognizer, IToken offendingSymbol,
            int line, int charPositionInLine, string msg, RecognitionException e)
        {
            ParserErrors++;
            ErrorList.Add(" Строка " + line + ":" + charPositionInLine + " [P] " + msg);
        }
    };
}
