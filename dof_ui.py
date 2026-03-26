import os
import threading
import subprocess
import tkinter as tk
from tkinter import ttk

from PIL import Image, ImageTk


ROOT_DIR = os.path.dirname(os.path.abspath(__file__))
EXE_PATH = os.path.join(ROOT_DIR, "build", "Release", "tinyraytracer.exe")


class DOFUI(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("Depth of Field - Progressive Lens Sampling UI")

        self.current_proc = None
        self.render_token = 0
        self.pending_after_id = None

        self.aperture_var = tk.DoubleVar(value=0.5)
        self.focus_var = tk.DoubleVar(value=14.0)

        self._build_ui()
        self._schedule_render(quality=0)  # quick initial render

    def _build_ui(self):
        main = ttk.Frame(self, padding=10)
        main.grid(row=0, column=0, sticky="nsew")
        self.columnconfigure(0, weight=1)
        self.rowconfigure(0, weight=1)

        controls = ttk.Frame(main)
        controls.grid(row=0, column=0, sticky="ew")
        controls.columnconfigure(1, weight=1)

        ttk.Label(controls, text="aperture").grid(row=0, column=0, sticky="w")
        # ttk.Scale 不同 Tk 版本支持项不一致（你这里不支持 resolution），因此用 tk.Scale
        tk.Scale(
            controls,
            variable=self.aperture_var,
            from_=0.0,
            to=1.0,
            resolution=0.01,
            orient="horizontal",
            length=480,
            command=lambda _v: self._on_change_slider(),
        ).grid(row=0, column=1, sticky="ew", padx=10)

        ttk.Label(controls, text="focus_dist").grid(row=1, column=0, sticky="w")
        tk.Scale(
            controls,
            variable=self.focus_var,
            from_=5.0,
            to=25.0,
            resolution=0.1,
            orient="horizontal",
            length=480,
            command=lambda _v: self._on_change_slider(),
        ).grid(row=1, column=1, sticky="ew", padx=10)

        btns = ttk.Frame(main)
        btns.grid(row=1, column=0, sticky="ew", pady=(10, 0))
        ttk.Button(btns, text="Full quality", command=lambda: self._schedule_render(quality=1)).pack(
            side="left"
        )
        ttk.Button(btns, text="Re-render (quick)", command=lambda: self._schedule_render(quality=0)).pack(
            side="left", padx=8
        )

        self.status = tk.StringVar(value="Rendering...")
        ttk.Label(main, textvariable=self.status).grid(row=2, column=0, sticky="w", pady=(8, 0))

        # Image view
        self.image_label = ttk.Label(main)
        self.image_label.grid(row=3, column=0, sticky="nsew", pady=(10, 0))

    def _on_change_slider(self):
        # Debounce while dragging.
        self._schedule_render(quality=0)

    def _schedule_render(self, quality: int):
        if self.pending_after_id is not None:
            self.after_cancel(self.pending_after_id)
        self.pending_after_id = self.after(200, lambda: self._start_render(quality))

    def _start_render(self, quality: int):
        self.pending_after_id = None

        self.render_token += 1
        my_token = self.render_token

        aperture = float(self.aperture_var.get())
        focus_dist = float(self.focus_var.get())

        # While interacting, render at low resolution to keep UI responsive.
        if quality == 0:
            width, height = 320, 240
        else:
            width, height = 1024, 768

        out_path = os.path.join(ROOT_DIR, f"ui_out_{my_token}.ppm")

        cmd = [
            EXE_PATH,
            "--aperture",
            str(aperture),
            "--focus_dist",
            str(focus_dist),
            "--width",
            str(width),
            "--height",
            str(height),
            "--out",
            out_path,
        ]

        # Kill previous render (best-effort) to avoid piling up.
        if self.current_proc is not None and self.current_proc.poll() is None:
            try:
                self.current_proc.terminate()
            except Exception:
                pass

        self.status.set(f"Rendering... token={my_token} (a={aperture:.3f}, f={focus_dist:.2f})")

        def run():
            try:
                proc = subprocess.Popen(cmd, cwd=ROOT_DIR, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                self.current_proc = proc
                proc.wait()
                if my_token != self.render_token:
                    return
                if not os.path.exists(out_path):
                    return

                img = Image.open(out_path).convert("RGB")
                # Show at UI scale (Tkinter will keep aspect).
                tk_img = ImageTk.PhotoImage(img)

                def update_ui():
                    if my_token != self.render_token:
                        return
                    self.image_label.configure(image=tk_img)
                    self.image_label.image = tk_img
                    self.status.set(f"Done (a={aperture:.3f}, f={focus_dist:.2f})")

                self.after(0, update_ui)
            except Exception as e:
                def update_err():
                    if my_token != self.render_token:
                        return
                    self.status.set(f"Render failed: {e}")
                self.after(0, update_err)

        threading.Thread(target=run, daemon=True).start()


if __name__ == "__main__":
    if not os.path.exists(EXE_PATH):
        raise SystemExit(f"Executable not found: {EXE_PATH}\nPlease build first (cmake --build ...).")
    app = DOFUI()
    app.mainloop()

