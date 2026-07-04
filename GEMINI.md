# Using Gemini CLI for Large Codebase Analysis

When analyzing large codebases or multiple files that might exceed context limits, use the Gemini CLI with its massive context window. Use `gemini -p` to leverage Google Gemini's large context capacity.

## File and Directory Inclusion Syntax

Use the `@` syntax to include files and directories in your Gemini prompts. Paths are relative to where you run the `gemini` command.

**Single file:**
```
gemini -p "@src/main.py Explain this file's purpose and structure"
```

**Multiple files:**
```
gemini -p "@package.json @src/index.js Analyze the dependencies used in the code"
```

**Entire directory:**
```
gemini -p "@src/ Summarize the architecture of this codebase"
```

**Multiple directories:**
```
gemini -p "@src/ @tests/ Analyze test coverage for the source code"
```

**Whole repo:**
```
gemini -p "@./ Give me an overview of this entire project"
# or
gemini --all_files -p "Analyze the project structure and dependencies"
```

## Implementation Verification Examples

```
gemini -p "@src/ Has dark mode been implemented? Show relevant files and functions"
gemini -p "@src/ @middleware/ Is JWT authentication implemented? List all auth-related endpoints"
gemini -p "@src/ @api/ Is proper error handling implemented for all API endpoints?"
gemini -p "@backend/ @middleware/ Is rate limiting implemented? Show the implementation details"
gemini -p "@src/ @lib/ @services/ Is Redis caching implemented? List all cache-related functions"
gemini -p "@src/ @api/ Are SQL injection protections implemented? Show how user inputs are sanitized"
gemini -p "@src/payment/ @tests/ Is the payment processing module fully tested? List all test cases"
```

## When to Use

Use `gemini -p` when:
- Analyzing entire codebases or large directories
- Comparing multiple large files
- Understanding project-wide patterns or architecture
- Context window is insufficient for the task
- Working with files totaling more than 100 KB
- Verifying whether specific features, patterns, or security measures are implemented

## Notes

- Paths in `@` syntax are relative to your current working directory when invoking `gemini`
- No `--yolo` flag needed for read-only analysis
- Gemini's context window can handle entire codebases that would overflow Claude's context
- Be specific about what you're looking for to get accurate results
