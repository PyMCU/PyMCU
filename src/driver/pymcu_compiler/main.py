import typer
from .commands.new import new
from .commands.build import build

app = typer.Typer(help="pymcu: Python-to-MCU compiler driver")

app.command()(new)
app.command()(build)

if __name__ == "__main__":
    app()
