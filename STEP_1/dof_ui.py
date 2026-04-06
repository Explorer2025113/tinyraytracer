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
        self.mode_var = tk.StringVar(value="progressive")  # progressive / fixed / pinhole
        self.fixed_samples_var = tk.IntVar(value=16)
        self.show_stage_var = tk.BooleanVar(value=False)
        self.mode_display = {
            "progressive": "Progressive lens sampling",
            "fixed": "Fixed lens sampling",
            "pinhole": "Pinhole camera",
        }
        self.mode_reverse = {v: k for k, v in self.mode_display.items()}

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

        ttk.Label(controls, text="Aperture radius (r)").grid(row=0, column=0, sticky="w")
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

        ttk.Label(controls, text="Focus distance (focus_dist)").grid(row=1, column=0, sticky="w")
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

        ttk.Label(controls, text="Camera mode").grid(row=2, column=0, sticky="w")
        mode_box = ttk.Combobox(
            controls,
            values=list(self.mode_display.values()),
            state="readonly",
            width=18,
        )
        mode_box.grid(row=2, column=1, sticky="w", padx=10)
        mode_box.set(self.mode_display[self.mode_var.get()])

        def on_mode_selected(_e):
            sel = mode_box.get()
            if sel in self.mode_reverse:
                self.mode_var.set(self.mode_reverse[sel])
            self._on_change_slider()

        mode_box.bind("<<ComboboxSelected>>", on_mode_selected)

        ttk.Label(controls, text="Fixed sample count (fixed mode)").grid(row=3, column=0, sticky="w")
        tk.Scale(
            controls,
            variable=self.fixed_samples_var,
            from_=1,
            to=32,
            resolution=1,
            orient="horizontal",
            length=480,
            command=lambda _v: self._on_change_slider(),
        ).grid(row=3, column=1, sticky="ew", padx=10)

        show_stage_cb = ttk.Checkbutton(
            controls, text="Show sampling stage heatmap (stage map)", variable=self.show_stage_var, command=self._on_change_slider
        )
        show_stage_cb.grid(row=4, column=0, columnspan=2, sticky="w", pady=(8, 0))

        btns = ttk.Frame(main)
        btns.grid(row=5, column=0, sticky="ew", pady=(10, 0))
        ttk.Button(btns, text="Full quality", command=lambda: self._schedule_render(quality=1)).pack(
            side="left"
        )
        ttk.Button(btns, text="Re-render (quick)", command=lambda: self._schedule_render(quality=0)).pack(
            side="left", padx=8
        )

        self.status = tk.StringVar(value="Rendering...")
        ttk.Label(main, textvariable=self.status).grid(row=6, column=0, sticky="w", pady=(8, 0))

        # Image view
        self.image_label = ttk.Label(main)
        self.image_label.grid(row=7, column=0, sticky="nsew", pady=(10, 0))

        self.stage_label = ttk.Label(main)
        self.stage_label.grid(row=8, column=0, sticky="nsew", pady=(0, 8))

        # Lens visualization (aperture disk)
        ttk.Label(main, text="Lens diagram: aperture size (radius r)").grid(row=9, column=0, sticky="w")
        vis_frame = ttk.Frame(main)
        vis_frame.grid(row=10, column=0, sticky="w", pady=(6, 10))
        self.lens_canvas = tk.Canvas(vis_frame, width=180, height=180, bg="#ffffff", highlightthickness=1)
        self.lens_canvas.pack(side="left")
        self.aperture_vis_text = tk.StringVar(value="")
        ttk.Label(vis_frame, textvariable=self.aperture_vis_text, justify="left").pack(side="left", padx=10)

        self._redraw_aperture_vis()

    def _on_change_slider(self):
        # Debounce while dragging.
        self._redraw_aperture_vis()
        self._schedule_render(quality=0)

    def _redraw_aperture_vis(self):
        # Visualize aperture radius on a disk: r in [0,1] -> pixel radius in [0,80]
        r = float(self.aperture_var.get())
        max_r = 80.0
        radius_px = max(0.0, min(max_r, r * max_r))

        cx = 90
        cy = 90
        d = radius_px * 2.0

        self.lens_canvas.delete("all")
        # Outer reference circle (unit aperture)
        unit_r = max_r
        self.lens_canvas.create_oval(cx - unit_r, cy - unit_r, cx + unit_r, cy + unit_r, outline="#cccccc")
        # Aperture disk
        if radius_px > 0.0:
            self.lens_canvas.create_oval(cx - radius_px, cy - radius_px, cx + radius_px, cy + radius_px, outline="#1f77b4", width=3)
        # Tkinter does not support #RRGGBBAA alpha colors; use a solid light fill instead.
            self.lens_canvas.create_oval(cx - radius_px, cy - radius_px, cx + radius_px, cy + radius_px, fill="#a9c9ff", outline="")
        else:
            # Pinhole-like: draw a small dot
            self.lens_canvas.create_oval(cx - 2, cy - 2, cx + 2, cy + 2, fill="#1f77b4", outline="")

        # Crosshair
        self.lens_canvas.create_line(cx - 95, cy, cx + 95, cy, fill="#bbbbbb")
        self.lens_canvas.create_line(cx, cy - 95, cx, cy + 95, fill="#bbbbbb")
        self.lens_canvas.create_text(cx, cy - 10, text="Lens plane", fill="#555555", font=("Arial", 12))

        self.aperture_vis_text.set(f"r={r:.2f} (aperture radius in this program)\nLarger r: stronger defocus blur")

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
        mode = str(self.mode_var.get())
        fixed_samples = int(self.fixed_samples_var.get())
        show_stage = bool(self.show_stage_var.get())

        # While interacting, render at low resolution to keep UI responsive.
        if quality == 0:
            width, height = 320, 240
        else:
            width, height = 1024, 768

        out_path = os.path.join(ROOT_DIR, "ui_out.ppm")
        stage_path = os.path.join(ROOT_DIR, "ui_stage.ppm")

        cmd = [
            EXE_PATH,
            "--mode",
            mode,
            "--aperture",
            str(aperture),
            "--focus_dist",
            str(focus_dist),
            "--width",
            str(width),
            "--height",
            str(height),
            "--samples",
            str(fixed_samples),
            "--out",
            out_path,
        ]

        if show_stage:
            cmd += ["--out_stage", stage_path]
        else:
            # Ensure we don't accidentally show old stage results.
            stage_path = None

        # Kill previous render (best-effort) to avoid piling up.
        if self.current_proc is not None and self.current_proc.poll() is None:
            try:
                self.current_proc.terminate()
            except Exception:
                pass

        mode = str(self.mode_var.get())
        self.status.set(f"Rendering... token={my_token} (mode={mode}, a={aperture:.3f}, focus={focus_dist:.2f})")

        def run():
            try:
                proc = subprocess.Popen(cmd, cwd=ROOT_DIR, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                self.current_proc = proc
                proc.wait()
                if my_token != self.render_token:
                    return
                if not os.path.exists(out_path):
                    return

                def update_ui():
                    if my_token != self.render_token:
                        return

                    img = Image.open(out_path).convert("RGB")
                    tk_img = ImageTk.PhotoImage(img)
                    self.image_label.configure(image=tk_img)
                    self.image_label.image = tk_img

                    if show_stage and stage_path and os.path.exists(stage_path):
                        st = Image.open(stage_path).convert("RGB")
                        tk_st = ImageTk.PhotoImage(st)
                        self.stage_label.configure(image=tk_st)
                        self.stage_label.image = tk_st
                    else:
                        self.stage_label.configure(image="")

                    self.status.set(f"Done (mode={mode}, a={aperture:.3f}, focus={focus_dist:.2f})")

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

