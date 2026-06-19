# RFC 0001 — ZCA con estado runtime: outlining y slots fijos

- Estado: **COMPLETO.** F1-F4 + factory multi-campo (sret) + arreglos de instancias `Class[N]`. Modelo A/B/C completos, outline por default. 351 frontend + 744 AVR verdes.
- Autor: (compiler)
- Fecha: 2026-06-10
- Afecta: `src/compiler/IR/IRGenerator/` (Scan, Call, Assign, Expr, State), AVR codegen, HAL `lib/src/pymcu/`

## 1. Problema

Una ZCA (`@inline class`) en PyMCU es **solo de compile-time**: sus constructores toman
`const[...]`, su estado se aplana a pseudo-variables (`sensor.name` -> `sensor_name`) que el
optimizador pliega a constante, y *no existe ninguna struct en SRAM* (`machine.py`:
"no stack frame, no SRAM instance struct"). En consecuencia el inline es **semánticamente
obligatorio**, no una optimización. Esto produce dos defectos:

1. **Factory cross-boundary roto.** `def setup() -> ADC: return ADC(Pin(14))` sin `@inline`
   falla con un link error críptico (`undefined reference to main.a_read`): no hay valor
   que retornar porque la instancia no existe en runtime.

2. **Bloat con N instancias.** Como el único modo de existir es "inlineado con el campo
   horneado como constante", `dht_a`, `dht_b`, `dht_c` replican toda la lógica de lectura
   N veces. Hoy el autor del driver lo evita **a mano**, partiendo el código en un dispatch
   fino `@inline` (`_avr_read(name: const[str])`) que delega en un worker no-inline
   `_pd_read(bit: uint8)`. Ese trabajo manual debería darlo el modelo.

3. **El decorador no significa nada.** `Scan.cs:442-450` mete los métodos **no-inline** en
   `instanceMethodDefs`, y `Call.cs:161-165` los **fuerza a inlinear** sobre instancias ZCA
   ("ZCA field aliasing requires inline expansion"). Es decir: hoy poner o no `@inline` en un
   método de clase da el mismo resultado. El decorador es un adorno.

## 2. Principio rector (cómo lo hacen C++/Rust/Zig)

En esos lenguajes el objeto **siempre tiene representación** (sus campos en memoria o
registros); los métodos son funciones reales que reciben `self`/`this`; e inlinear/plegar a
constante es decisión del **optimizador**, no de la semántica. El "costo cero" es el
*resultado* de que el optimizador pruebe que la instancia es conocida (Rust ZST por typestate,
C++ `constexpr`, Zig `comptime`). Cuando no puede probarlo (N instancias, `dyn Trait`), el
campo vive en memoria y **una** copia del método lo lee.

PyMCU invirtió esto. La corrección: el ZCA debe poder tener representación runtime, y el
`@inline` debe volver a *elegir* entre "plegar a constante" (costo cero, singleton caliente)
y "compartir una función" (poco código, N instancias).

## 3. Diseño: dos modelos de materialización

Una instancia ZCA se materializa de una de dos formas, decididas por el compilador con una
regla simple y predecible:

### Modelo A — *unboxed* (campos como parámetros)  [caso común, prototipo]

Cuando un método **no** es `@inline` y la instancia **no escapa** (no se retorna, no se mete
en un arreglo, no se pasa como objeto a otra función), el método se compila **una vez** como
función real cuya firma son los **campos runtime** de la instancia:

```
# fuente
class DHT11:
    def __init__(self, pin: uint8):   # ya NO const
        self.pin = pin
    def read(self) -> uint16:         # sin @inline -> outlined
        ...usa self.pin...

a = DHT11(2); b = DHT11(3)
a.read(); b.read()
```

```
# IR resultante
func DHT11_read(pin: uint8) -> uint16 { ... usa 'pin' ... }   # UNA copia
main:
  call DHT11_read(2)
  call DHT11_read(3)
```

- **Cero SRAM**: la instancia no tiene slot; sus campos viajan en registros (calling
  convention actual: arg0->R24, arg1->R22, ...).
- Reusa toda la maquinaria de `functionsToCompile` y de emisión de `Call`.
- Ideal para el caso dominante (campos de 1-2 bytes). Es exactamente lo que `_pd_read(bit)`
  hace a mano, pero **automático**.

Transformación clave (scalar replacement of aggregates + outlining):
1. Recolectar el conjunto de `self.<campo>` que el cuerpo lee/escribe -> lista ordenada de
   params `(campo, tipo)`.
2. Reescribir el cuerpo: `self.<campo>` -> referencia al param homónimo.
3. Emitir `Class_method(<campos...>)` una vez vía `functionsToCompile`.
4. En cada call site `inst.method(args)`: pasar primero los **valores runtime de los campos
   de `inst`**, luego los `args` del usuario.

### Modelo B — *boxed* (slot fijo + puntero self)  [escape, factories, colecciones]

Cuando la instancia **escapa** (se retorna desde una función, entra en un `Class[N]`, se pasa
como objeto), se le asigna un **slot estático en SRAM** (sin heap: una reserva fija por
instancia declarada, igual que `moduleSramArrays`) que guarda sus campos empaquetados:

```
# layout del slot (ejemplo DHT11): offset 0 = pin (u8)
.dseg
inst_a: .byte 1
```

- El constructor escribe los campos en el slot.
- Los métodos no-inline se compilan una vez con un **param `self` = dirección del slot**
  (R24:R25 = puntero), y leen campos con `LD r, Z+offset`.
- `inst.method()` -> cargar dirección del slot en R24:R25, `call Class_method`.
- Una factory `def setup() -> DHT11` retorna **el puntero al slot** en R24:R25 — ahora sí hay
  un valor real que cruza la frontera. Resuelve el bug #1.

Model B es estrictamente más general que A pero paga un puntero + accesos a SRAM. Se usa solo
cuando A no aplica.

### Modelo C — *collapsed* (lo actual, renombrado)

Cuando el método **es** `@inline` (o la instancia es un singleton y el optimizador prueba que
los campos son constantes), se mantiene el comportamiento actual: inline + constant-folding,
el campo se hornea, costo cero real. Es el camino para `led.on()`, `uart.write()`, etc.

## 4. Regla de selección (determinista, sin heurística mágica)

```
para cada instancia I de clase C:
  si algún método llamado sobre I es @inline           -> esa llamada es Modelo C (colapsa)
  si I escapa (return / Class[N] / pasada como objeto)  -> Modelo B (slot + self-ptr)
  si no                                                 -> Modelo A (campos como params)
```

`@inline` en el método sigue mandando: fuerza C para esa llamada. Un método **sin** `@inline`
deja de auto-inlinearse (se elimina el force-inline de `Call.cs:161-165` para el caso ZCA) y
pasa a A o B. **Así el decorador recupera su significado**: sin él, el método se comparte.

> Nota de compatibilidad: hoy TODO método de HAL es `@inline`, así que todo el HAL existente
> permanece en Modelo C sin cambios. A/B solo se activan cuando el usuario (o el driver)
> deliberadamente omite `@inline` para compartir código.

## 5. Cambios en el IR / IRGenerator

| Componente | Cambio |
|---|---|
| `State.cs` | `instanceFieldLayout: Dictionary<class, List<(field,type)>>`; `instanceSlots: Dictionary<instance, sramLabel>`; `escapingInstances: HashSet<instance>`. |
| `Scan.cs` | Al registrar método no-inline de clase: además de `instanceMethodDefs`, marcar como *outlineable* y derivar el layout de campos desde el `__init__`. |
| Escape analysis | Pase ligero post-scan: marcar `escapingInstances` (aparece en `ReturnStmt`, en `Class[N]` store, o como arg a función no-inline cuyo param es de tipo clase). |
| `Call.cs` | Sustituir el force-inline (`161-165`) por: si método no-inline y instancia no-escapa -> emitir `Call(Class_method, [fieldVals... , args...])` (Modelo A); si escapa -> `Call(Class_method, [slotAddr, args...])` (Modelo B). |
| `Assign.cs` | `__init__`: en A no emite nada (los campos quedan como valores SSA de la instancia); en B emite stores al slot. Reservar slot en B. |
| `Expr.cs` | Dentro del cuerpo outlined, `self.<campo>` resuelve al param (A) o a `LD Z+off` (B). |
| AVR codegen | B: emitir `.byte` del slot en `.dseg`, y prólogo que mueve R24:R25 -> Z para acceso a campos. A no requiere codegen nuevo (función normal). |

## 6. Cambios en el HAL

Mínimos y opt-in. Para que un campo sea runtime, su constructor debe aceptar el tipo runtime,
no solo `const`:

```python
class DHT11:
    def __init__(self, pin: uint8):   # admite runtime; const sigue plegando en Modelo C
        self.pin = pin
```

Se mantiene una sobrecarga `const[...]` cuando se quiere garantizar el colapso. El resto del
HAL no cambia (sigue en Modelo C).

## 7. Plan por fases

- **F1 (HECHO):** Modelo A end-to-end. Decorador `@outline` (Parser/Ast + marcador stdlib),
  detección y síntesis en `Scan.cs` (`DeriveFieldLayout` + outline branch), dispatch en
  `Call.cs`. Hallazgo clave: como `self.<field>` aplana a `self_<field>` y los params se
  cualifican como `<func>.self_<field>`, basta nombrar el param `self_<field>` -- el cuerpo
  resuelve solo, **sin reescritura de AST**. Fixture `zca-outline` + test verde: dos
  instancias, **una** `Counter_stepped` con `args=[campo, k]`. 351 frontend + 740 AVR verdes.
- **F2 (HECHO):** DHT-style escrito de forma natural (protocolo completo en `read(self)` sobre
  `self.pin`, sin el split manual `_avr_read`/`_pd_read`). Fixture `zca-outline-dht`, 3 sensores
  (pines 2/3/4):
  - `@inline`: **3596 B**, protocolo duplicado 3x.
  - `@outline`: **1268 B**, una copia de `DHT_read` + 3 `CALL`.
  - **-2328 B (2.8x)**, y la ventaja crece lineal con cada instancia.
- **F3 (parcial -- HECHO la variante en registro):** Modelo B con **handle empaquetado en
  registro** para ZCA de **un solo campo primitivo**. Un factory no-`@inline`
  `def make() -> Sensor: return Sensor(base+1)` retorna el campo como escalar en el registro
  de retorno (sin SRAM); el use site marca `s = make()` como *handle instance* y `s.read()`
  (que debe ser `@outline`) recibe ese escalar como su campo. **Arregla el bug del factory sin
  forzar `@inline`** (antes: `undefined reference to <var>_read`). Fixture `zca-factory-b`,
  test verde; 351 frontend + 741 AVR.
  - Cambios: `zcaFactoryClasses`/`factoryHandleInstances`/`classFieldLayout` (State); layout con
    `SourceParam` + registro de clases de campo único (Scan); lowering de `return C(args)` ->
    `return <campo>` y return type IR = tipo del campo (Statements); tracking del handle
    (Assign); arg del campo = la variable handle en el dispatch `@outline` (Call).
- **F3-slot (HECHO -- construcción directa):** Modelo B con **slot fijo en SRAM** para ZCA de
  **>= 2 campos** (no caben en el registro de retorno). La instancia se "encajona": sus campos
  viven en un slot SRAM y su método `@outline` toma un puntero `self`, leyendo cada campo con
  `BytearrayLoad` a su byte-offset. Fixture `zca-slot`: `a=Sensor(3,4)` y `b=Sensor(5,7)` ->
  dos slots de 2 B distintos, **un** `Sensor_read(self)` compartido que recorre el puntero ->
  12 y 35. Reusa IR existente (`ArrayStore` init, `ArrayBase` para pasar la dirección,
  `BytearrayLoad` ptr+offset) -- **sin cambios en el backend AVR**; el backend ya aloja el slot
  por uso (`.equ main_a__slot, _stack_base + 0`). 351 frontend + 742 AVR verdes.
  - Opt-in: una clase es *slot class* solo si tiene >= 2 campos **y** un método `@outline`
    (las clases HAL `@inline` multi-campo conservan su construcción virtual -- gating crítico,
    sin él 46 tests rompen al secuestrar `Pin(13)` etc.).
  - Cambios: `slotClasses`/`slotInstances`/`slotMethods`/`slotMethodFieldOffsets` (State);
    branch slot en la síntesis `@outline` con param `self: bytearray` + offsets (Scan);
    `EmitSlotConstruction` con early-return, aislado de la maquinaria del constructor (Assign);
    `self.<campo>` -> `BytearrayLoad(self, off)` (Expr); dispatch pasa `ArrayBase(slot)` (Call).
- **F3-slot-factory (HECHO -- sret):** factory no-`@inline` que retorna un ZCA **multi-campo**.
  Como no cabe en el registro de retorno, usa **sret**: el *caller* asigna el slot SRAM de la
  instancia y pasa su dirección como un puntero oculto `__self` (primer arg); la factory
  almacena cada campo con `BytearrayStore(__self, off, arg)` y **retorna el puntero**. Dos
  llamadas => dos slots distintos (sin aliasing: cada slot lo posee el caller). Fixture
  `zca-factory-slot`: `make(3,4)`/`make(5,7)` -> 12/35. 351 + 743 verdes.
  - Cambios: `VisitFunction` inyecta el param `__self: bytearray` y fija el return type IR a
    puntero (`UINT16`) para factories de slot class; `VisitReturn` baja `return C(args)` a
    `BytearrayStore`s + `return __self`; `EmitSlotFactoryCall` (Assign) asigna el slot en el
    call site, pasa `ArrayBase(slot)` y rastrea la instancia; el optimizer cuenta `ArrayBase`
    como uso vivo en el análisis de dead-globals (si no, el slot -- escrito solo por puntero
    dentro de la factory -- se elimina y da link error `undefined main_s__slot`).
- **Class[N] (HECHO):** arreglo de instancias ZCA encajonadas, contiguas en SRAM (el caso
  "multiples DHT"). `arr: Sensor[3]` reserva `N*stride` bytes; `arr[i] = Sensor(p,g)` construye
  en el elemento i (stores a `i*stride + offset`, indice constante o runtime); `arr[i].read()`
  computa `base + i*stride` y llama al **mismo** `Sensor_read` compartido pasando esa direccion
  como self. Fixture `zca-array`: 3 sensores, un cuerpo, loop runtime-indexado -> 12/35/18.
  351 + 744 verdes.
  - Cambios: `instanceArrayClass`/`instanceArrayStride` (State); decl de array-de-slot-class
    (Assign, antes del path `T[N]`); `EmitInstanceArrayStore` (construccion por elemento);
    rama `IndexExpr`-como-objeto en el dispatch (Call) que computa `base + i*stride` con
    `ArrayBase` + `Binary` y lo pasa como self-ptr.
- **F4 (HECHO):** **el outlining es ahora el default.** Un método de clase **sin** `@inline` se
  **outlinea automáticamente** cuando es *outline-safe* (`IsOutlineSafe`: el cuerpo toca `self`
  solo como `self.<campo>` con `<campo>` un dato derivable; nunca `self.<metodo>()` ni `self`
  desnudo; cualquier nodo no reconocido => unsafe, conservador). Si es safe => Model A/B/slot
  vía `RegisterOutlinedMethod`; si no => se mantiene el force-inline (`Call.cs`) como **fallback**
  -- es la única forma de darle representación a un método que usa `self` de forma irreducible
  (métodos heredados que llaman `self.otro()`, etc.). **Así `@inline` por fin significa algo**:
  sin él, un método representable se comparte, no se inlinea en silencio por instancia.
  - `@outline` queda **retirado de la necesidad**: los 4 fixtures se reescribieron sin el
    decorador y outlinean igual (DHT 3 sensores = 1268 B, idéntico). Sobrevive solo como
    *override explícito* (forzar compartir cuando la heurística conservadora se queda corta,
    p.ej. un cuerpo con `for`/`match` que `IsOutlineSafe` no recorre) -- el análogo de
    `#[inline(never)]`.
  - Hallazgo: los decls locales tipados (`x: T = expr`) parsean como `VarDecl`, no `AnnAssign`;
    `IsOutlineSafe` debe manejarlo o rechaza métodos triviales (rompía DHT.read). 351 + 742 verdes.
  - **Pendiente:** diagnóstico claro cuando una factory multi-campo (Modelo B sret) o un
    `Class[N]` aún no soportado se usa, en vez del link error.

## 8. Alternativas descartadas

- **Forzar `@inline` siempre** (estado actual): anula el decorador, causa bloat. Rechazado por
  diseño.
- **Error duro "factory debe ser @inline"**: respeta el decorador pero no resuelve el bloat de
  N instancias, que es el problema real. Insuficiente.
- **Heap / objetos dinámicos**: incompatible con bare-metal sin allocator. Descartado.

## 9. Cuestiones abiertas

- Layout de campos con tipos mixtos (alineación en el slot de Model B).
- Instancias que mezclan llamadas `@inline` y no-inline: ¿forzar B para toda la instancia o
  permitir colapso por-llamada? (Propuesta: colapso por-llamada; B solo si escapa.)
- Herencia: el layout debe incluir campos de bases (MRO) — ya hay `ResolveMROMethod`.
