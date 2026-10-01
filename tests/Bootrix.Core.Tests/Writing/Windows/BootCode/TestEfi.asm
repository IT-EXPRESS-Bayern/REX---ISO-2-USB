; The smallest EFI application that proves it was started: it writes a line to the first serial port
; and halts. A PE32+ image for x86-64, built as one flat file; no UEFI services are used, so it does
; not depend on anything but the firmware having loaded and entered the image. Assembled by the
; tests with: nasm -f bin
bits 64
default rel

IMAGE_BASE      equ 0x140000000
SECTION_RVA     equ 0x1000
FILE_ALIGN      equ 0x200

; --- DOS header ------------------------------------------------------------------------------
        db 'MZ'
        times 0x3C - 2 db 0
        dd pe_header                    ; e_lfanew

; --- PE header -------------------------------------------------------------------------------
pe_header:
        db 'PE', 0, 0
        dw 0x8664                       ; machine: x86-64
        dw 1                            ; sections
        dd 0, 0, 0
        dw optional_end - optional      ; size of optional header
        dw 0x0022                       ; executable, large address aware

optional:
        dw 0x020B                       ; PE32+
        db 14, 0                        ; linker version
        dd FILE_ALIGN                   ; size of code
        dd 0, 0                         ; initialised and uninitialised data
        dd SECTION_RVA                  ; entry point
        dd SECTION_RVA                  ; base of code
        dq IMAGE_BASE
        dd 0x1000                       ; section alignment
        dd FILE_ALIGN                   ; file alignment
        dw 0, 0, 0, 0, 6, 0             ; OS and image versions, subsystem version 6.0
        dd 0                            ; win32 version
        dd 0x2000                       ; size of image
        dd FILE_ALIGN                   ; size of headers
        dd 0                            ; checksum
        dw 10                           ; subsystem: EFI application
        dw 0
        dq 0x100000, 0x1000, 0x100000, 0x1000
        dd 0
        dd 16                           ; data directories
        times 16 * 8 db 0
optional_end:

; --- section table ---------------------------------------------------------------------------
        db '.text', 0, 0, 0
        dd code_end - code_start        ; virtual size
        dd SECTION_RVA
        dd FILE_ALIGN                   ; raw size
        dd FILE_ALIGN                   ; raw offset
        dd 0, 0
        dw 0, 0
        dd 0x60000020                   ; code, executable, readable

        times FILE_ALIGN - ($ - $$) db 0

; --- code ------------------------------------------------------------------------------------
code_start:
        mov dx, 0x3F8
        lea rsi, [message]
.next:
        lodsb
        test al, al
        jz .halt
        out dx, al
        jmp .next
.halt:
        hlt
        jmp .halt
message: db 13, 10, 'BOOTRIX-EFI-TEST-STARTED', 13, 10, 0
code_end:
        times 2 * FILE_ALIGN - ($ - $$) db 0
