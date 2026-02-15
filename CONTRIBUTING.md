# Contributing to PyMCU

Thank you for your interest in contributing to PyMCU! We welcome bug reports, feature requests, and pull requests.

## Code of Conduct

Please note that this project is released with a [Code of Conduct](CODE_OF_CONDUCT.md). By participating in this project you agree to abide by its terms.

## Development Setup

1.  **Clone the repository:**
    ```bash
    git clone https://github.com/begeistert/pymcu-compiler.git
    cd pymcu-compiler
    ```

2.  **Install dependencies (using `uv` is recommended):**
    ```bash
    uv sync --dev
    ```

3.  **Run tests:**
    ```bash
    pytest tests/
    ```

## Commit Guidelines

We follow the **Conventional Commits** specification. This leads to more readable messages that are easy to follow when looking through the project history.

**Format:** `<type>[optional scope]: <description>`

**Examples:**
- `feat(compiler): add support for floating point literals`
- `fix(driver): resolve crash on missing config`
- `docs: update installation instructions`
- `style: format code with black`
- `refactor: simplify AST traversal logic`
- `test: add unit tests for lexer`
- `chore: update dependencies`

**Types:**
- `feat`: A new feature
- `fix`: A bug fix
- `docs`: Documentation only changes
- `style`: Changes that do not affect the meaning of the code (white-space, formatting, etc)
- `refactor`: A code change that neither fixes a bug nor adds a feature
- `perf`: A code change that improves performance
- `test`: Adding missing tests or correcting existing tests
- `chore`: Changes to the build process or auxiliary tools and libraries such as documentation generation

## Pull Requests

1.  Fork the repository and create your branch from `main`.
2.  If you've added code that should be tested, add tests.
3.  Ensure the test suite passes.
4.  Make sure your code lints.
5.  Issue that pull request!

## License

By contributing, you agree that your contributions will be licensed under its MIT License.
