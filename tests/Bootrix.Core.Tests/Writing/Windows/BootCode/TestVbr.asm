; Stand-in for the boot sector chain of a Windows FAT32 volume: sector 0 loads sector 12 of the
; volume (LBA = hidden sectors from the BPB + 12, drive number from the BPB) and runs it, the way the
; NT boot code does. Sector 12 prints a line. It proves that the BPB, the reserved-area layout and the
; MBR above it agree with what real boot code reads. Assembled by the tests with: nasm -f bin
bits 16

section .sector0 start=0 vstart=0x7C00
        jmp short start
        nop
        times 0x5A - ($ - $$) db 0
start:
        cli
        xor ax, ax
        mov ds, ax
        mov es, ax
        mov ss, ax
        mov sp, 0x7C00
        sti
        mov eax, [0x7C00 + 0x1C]        ; hidden sectors
        add eax, 12
        mov [dap_lba], eax
        mov dl, [0x7C00 + 0x40]         ; drive number of a FAT32 BPB
        mov si, dap
        mov ah, 0x42
        int 0x13
        jc fail
        jmp 0x0000:0x8000
fail:
        mov si, failed
        call print
        jmp $
print:
        lodsb
        test al, al
        jz .done
        mov ah, 0x0E
        mov bx, 7
        int 0x10
        jmp print
.done:
        ret
failed: db 'VBR READ FAILED', 0
loader: db 'BOOTMGR    '            ; the name NT 6 boot code looks for in the root directory
dap:    db 0x10, 0
        dw 1
        dw 0x8000, 0
dap_lba: dq 0
        times 510 - ($ - $$) db 0
        dw 0xAA55

section .sector12 start=0x1800 vstart=0x8000
        mov si, ok
.next:
        lodsb
        test al, al
        jz .halt
        mov ah, 0x0E
        mov bx, 7
        int 0x10
        jmp .next
.halt:
        hlt
        jmp .halt
ok:     db 'VBR SECTOR 12 REACHED', 0
