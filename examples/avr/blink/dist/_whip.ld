OUTPUT_FORMAT("elf32-avr","elf32-avr","elf32-avr")
OUTPUT_ARCH(avr:5)
ENTRY(main)
SECTIONS
{
  .text 0x000000 :
  {
    *(.vectors)
    *(.text*)
    *(.rodata*)
    . = ALIGN(2);
  }
  .data 0x800100 :
  {
    *(.data*)
    *(.bss*)
    *(COMMON)
    . = ALIGN(1);
  }
}
