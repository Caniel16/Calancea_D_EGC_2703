// Varianta C: roata panoramică
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Egc;

/// <summary>
/// Tema laboratorului 6, varianta C: roata panoramică.
///
/// Ierarhia (secțiunea 6):
///   butuc (pivot, se rotește în jurul axei Z)
///    ├─ forma butucului
///    ├─ spițe și jantă (pe două fețe, z = ±PlaneOffset): pivot rotit + formă-copil scalată
///    ├─ barele de prindere ale cabinelor
///    └─ cabina k (pivot în punctul de prindere de pe jantă)
///         ├─ tija, acoperișul, corpul cabinei (forme-copil)
///   Pivotul cabinei are rotația -unghiulRoții (+ leagănul): rotațiile se anulează în lume,
///   deci cabina rămâne verticală orice ar face roata.
///
/// Mișcarea:
///   - săgețile sus / jos (ținute) schimbă viteza țintă; viteza reală se apropie lin de ea:
///     v ← v + (v_țintă − v) · (1 − e^(−k·dt)), același rezultat la orice număr de cadre pe secundă;
///   - Space oprește roata lin (viteza țintă devine 0) și, apăsată din nou, o pornește cu viteza de dinainte;
///   - tastele 1-8: roata frânează lin, apoi un Tween (EaseInOut) pe unghiul roții aduce cabina
///     aleasă jos, la peron, pe drumul cel mai scurt (cel mult 180°);
///   - bonus: cabinele se leagănă când roata accelerează / frânează (resort amortizat, pendul liniarizat).
/// Toate calculele folosesc doar timpul de joc dt, deci pauza, scara timpului și blocarea (B) funcționează.
/// </summary>
public sealed class FerrisWheelScene : Scene
{
    // --- Acțiunile scenei (nume unice: Input este comun tuturor scenelor) ---------------------
    private const string SpeedUp = "roata: viteza țintă mai mare (ține)";
    private const string SpeedDown = "roata: viteza țintă mai mică (ține)";
    private const string StopResume = "roata: oprește lin / pornește din nou";

    private const int CabinCount = 8;
    private static readonly string[] BringCabin =
        Enumerable.Range(1, CabinCount).Select(k => $"roata: cabina {k} la peron").ToArray();

    // --- Dimensiuni ---------------------------------------------------------------------------
    private const float Radius = 4.0f;          // raza jantei (punctele de prindere ale cabinelor)
    private const float HubHeight = 6.0f;       // înălțimea axului
    private const float PlaneOffset = 0.6f;     // cele două fețe ale roții sunt la z = ±PlaneOffset
    private const int RimSegments = 32;
    private const float CabinStep = 360.0f / CabinCount;

    // --- Viteza -------------------------------------------------------------------------------
    private const float MaxSpeed = 45.0f;       // grade pe secundă
    private const float SpeedChangeRate = 20.0f;// cu cât se schimbă viteza țintă într-o secundă de ținere
    private const float SmoothK = 2.0f;         // k din formula de apropiere (mai mare = mai rapid)
    private const float ParkStartSpeed = 0.5f;  // sub această viteză începe aducerea cabinei la peron

    // --- Leagănul cabinelor (bonus) -----------------------------------------------------------
    private const float HangLength = 1.0f;      // distanța de la punctul de prindere la centrul cabinei
    private const float Gravity = 9.81f;
    private const float SwingDamping = 0.12f;   // amortizarea relativă (ζ)
    private const float MaxSwing = 35.0f;       // grade
    private const float SwingStrength = 0.5f;   // cât de tare simt cabinele accelerația (1 = valoarea fizică)

    private SceneObject _hub = null!;
    private readonly SceneObject[] _cabins = new SceneObject[CabinCount];
    private readonly float[] _swing = new float[CabinCount];        // unghiul de leagăn, în radiani
    private readonly float[] _swingSpeed = new float[CabinCount];   // rad/s

    private float _angle;                 // unghiul roții, în grade (pozitiv = sens trigonometric)
    private float _speed;                 // viteza reală, grade/s
    private float _targetSpeed = 15.0f;   // roata pornește lin la încărcarea scenei
    private float _resumeSpeed = 15.0f;   // viteza la care repornește după Space
    private float _omega;                 // viteza unghiulară din ultimul cadru (grade/s), și în timpul Tween-ului
    private float _alpha;                 // accelerația unghiulară (grade/s²), pentru leagăn

    private Tween? _park;                 // Tween-ul pe unghi (doar componenta X este folosită)
    private int _parkCabin = -1;          // cabina cerută la peron (-1 = niciuna)

    public override string Name => "tema: roata panoramică";

    public override IEnumerable<string> ActionNames =>
        new[] { SpeedUp, SpeedDown, StopResume }.Concat(BringCabin);

    public override string Status
    {
        get
        {
            string state = _park != null ? $"cabina {_parkCabin + 1} vine la peron"
                         : _parkCabin >= 0 ? $"frânează pentru cabina {_parkCabin + 1}"
                         : $"la peron: cabina {NearestCabin() + 1}";
            return $"v = {_omega:0.0}°/s, țintă {_targetSpeed:0.0}°/s | {state}";
        }
    }

    // =====================================================================================
    // Construirea scenei
    // =====================================================================================

    public override void Load(Input input)
    {
        CameraPosition = new Vector3(6.0f, 6.5f, 16.0f);
        CameraTarget = new Vector3(0.0f, 4.6f, 0.0f);

        input.Bind(SpeedUp, Keys.Up);
        input.Bind(SpeedDown, Keys.Down);
        input.Bind(StopResume, Keys.Space);
        for (int k = 0; k < CabinCount; k++)
        {
            input.Bind(BringCabin[k], Keys.D1 + k, Keys.KeyPad1 + k);
        }

        Mesh grid = AddMesh(Shapes.Grid(12, 1.0f, new Color4(0.30f, 0.33f, 0.38f, 1.0f)));
        Mesh cube = AddMesh(Shapes.ShadedCube());
        Mesh cylinder = AddMesh(Shapes.ShadedCylinder(0.5f, 1.0f, 24));   // diametru 1, înălțime 1, axa Y

        AddObject("podea", grid);
        BuildStructure(cube, cylinder);
        BuildWheel(cube, cylinder);
        BuildCabins(cube);
        UpdateTransforms();
    }

    /// <summary>Părțile fixe: peronul, picioarele (două perechi în formă de A) și axul.</summary>
    private void BuildStructure(Mesh cube, Mesh cylinder)
    {
        SceneObject platform = AddObject("peron", cube);
        platform.Transform.Position = new Vector3(0.0f, 0.25f, 0.0f);
        platform.Transform.Scale = new Vector3(2.6f, 0.5f, 2.4f);
        platform.Tint = new Color4(0.6f, 0.5f, 0.4f, 1.0f);

        const float footX = 2.8f;
        float legLength = MathF.Sqrt(footX * footX + HubHeight * HubHeight);
        float legAngle = MathF.Atan2(footX, HubHeight) * 180.0f / MathF.PI;
        foreach (float z in new[] { -PlaneOffset - 0.5f, PlaneOffset + 0.5f })
        {
            foreach (float side in new[] { -1.0f, 1.0f })
            {
                // Piciorul unește (side · footX, 0) cu axul (0, HubHeight): centrul la mijloc, înclinat.
                SceneObject leg = AddObject("picior", cube);
                leg.Transform.Position = new Vector3(side * footX / 2.0f, HubHeight / 2.0f, z);
                leg.Transform.Rotation = new Vector3(0.0f, 0.0f, side * legAngle);
                leg.Transform.Scale = new Vector3(0.25f, legLength, 0.25f);
                leg.Tint = new Color4(0.75f, 0.75f, 0.8f, 1.0f);
            }
        }

        SceneObject axle = AddObject("ax", cylinder);
        axle.Transform.Position = new Vector3(0.0f, HubHeight, 0.0f);
        axle.Transform.Rotation = new Vector3(90.0f, 0.0f, 0.0f);     // axa cilindrului: Y -> Z
        axle.Transform.Scale = new Vector3(0.3f, 2.0f * PlaneOffset + 1.4f, 0.3f);
        axle.Tint = new Color4(0.5f, 0.5f, 0.55f, 1.0f);
    }

    /// <summary>Partea care se rotește: butucul (pivot) și, ca descendenți, spițele, janta și barele.</summary>
    private void BuildWheel(Mesh cube, Mesh cylinder)
    {
        _hub = AddObject("butuc (pivot)", null);
        _hub.Transform.Position = new Vector3(0.0f, HubHeight, 0.0f);

        SceneObject hubShape = AddObject("butuc", cylinder, _hub);
        hubShape.Transform.Rotation = new Vector3(90.0f, 0.0f, 0.0f);
        hubShape.Transform.Scale = new Vector3(0.9f, 2.0f * PlaneOffset + 0.2f, 0.9f);
        hubShape.Tint = new Color4(0.9f, 0.3f, 0.3f, 1.0f);

        var spokeColor = new Color4(0.95f, 0.95f, 1.0f, 1.0f);
        var rimColor = new Color4(1.0f, 0.75f, 0.25f, 1.0f);
        float chord = 2.0f * Radius * MathF.Sin(MathF.PI / RimSegments) + 0.03f;   // puțin mai lung: fără goluri
        float rimDistance = Radius * MathF.Cos(MathF.PI / RimSegments);             // de la centru la mijlocul coardei

        foreach (float z in new[] { -PlaneOffset, PlaneOffset })
        {
            // Spițele: un pivot rotit pentru fiecare; forma (un cub lung și subțire) este copilul lui.
            // Scalarea neuniformă stă pe formă, nu pe pivot (secțiunea 6).
            for (int i = 0; i < CabinCount; i++)
            {
                SceneObject spokePivot = AddObject($"spiță {i} (pivot)", null, _hub);
                spokePivot.Transform.Position = new Vector3(0.0f, 0.0f, z);
                spokePivot.Transform.Rotation = new Vector3(0.0f, 0.0f, CabinAngle(i));

                SceneObject spoke = AddObject($"spiță {i}", cube, spokePivot);
                spoke.Transform.Position = new Vector3(Radius / 2.0f, 0.0f, 0.0f);
                spoke.Transform.Scale = new Vector3(Radius, 0.08f, 0.08f);
                spoke.Tint = spokeColor;
            }

            // Janta: RimSegments segmente drepte, fiecare tangent la cerc în mijlocul arcului său.
            for (int j = 0; j < RimSegments; j++)
            {
                SceneObject rimPivot = AddObject($"jantă {j} (pivot)", null, _hub);
                rimPivot.Transform.Position = new Vector3(0.0f, 0.0f, z);
                rimPivot.Transform.Rotation = new Vector3(0.0f, 0.0f, (j + 0.5f) * 360.0f / RimSegments);

                SceneObject rim = AddObject($"jantă {j}", cube, rimPivot);
                rim.Transform.Position = new Vector3(rimDistance, 0.0f, 0.0f);
                rim.Transform.Scale = new Vector3(0.12f, chord, 0.12f);
                rim.Tint = rimColor;
            }
        }

        // Barele dintre cele două fețe, de care atârnă cabinele.
        for (int i = 0; i < CabinCount; i++)
        {
            SceneObject bar = AddObject($"bara cabinei {i + 1}", cube, _hub);
            bar.Transform.Position = PointOnRim(i);
            bar.Transform.Scale = new Vector3(0.1f, 0.1f, 2.0f * PlaneOffset);
            bar.Tint = spokeColor;
        }
    }

    /// <summary>Cele 8 cabine: un pivot în punctul de prindere (copilul butucului) și formele lui.</summary>
    private void BuildCabins(Mesh cube)
    {
        for (int i = 0; i < CabinCount; i++)
        {
            SceneObject anchor = AddObject($"cabina {i + 1} (pivot)", null, _hub);
            anchor.Transform.Position = PointOnRim(i);
            _cabins[i] = anchor;

            Color4 color = ColorUtil.FromHsv(i / (float)CabinCount, 0.65f, 1.0f);

            SceneObject rod = AddObject($"tija cabinei {i + 1}", cube, anchor);
            rod.Transform.Position = new Vector3(0.0f, -0.35f, 0.0f);
            rod.Transform.Scale = new Vector3(0.06f, 0.7f, 0.06f);
            rod.Tint = new Color4(0.8f, 0.8f, 0.85f, 1.0f);

            SceneObject roof = AddObject($"acoperișul cabinei {i + 1}", cube, anchor);
            roof.Transform.Position = new Vector3(0.0f, -0.68f, 0.0f);
            roof.Transform.Scale = new Vector3(1.0f, 0.1f, 0.9f);
            roof.Tint = new Color4(color.R * 0.6f, color.G * 0.6f, color.B * 0.6f, 1.0f);

            SceneObject body = AddObject($"cabina {i + 1}", cube, anchor);
            body.Transform.Position = new Vector3(0.0f, -HangLength - 0.05f, 0.0f);
            body.Transform.Scale = new Vector3(0.9f, 0.7f, 0.8f);
            body.Tint = color;
        }
    }

    // =====================================================================================
    // Actualizarea, la fiecare cadru
    // =====================================================================================

    public override void Update(float dt, Input input)
    {
        base.Update(dt, input);
        if (dt <= 0.0f) return;   // pauză: totul rămâne pe loc (și leagănul)

        HandleInput(dt, input);

        // 1. Pasul unghiului în acest cadru: din Tween (aducerea la peron) sau din viteza netezită.
        float step;
        if (_park != null)
        {
            float before = _park.Value.X;
            _park.Update(dt);
            step = _park.Value.X - before;
            if (_park.IsFinished)
            {
                ConsolePanel.PrintMessage($"cabina {_parkCabin + 1} a ajuns la peron");
                _park = null;
                _parkCabin = -1;
                _speed = 0.0f;
            }
        }
        else
        {
            // Apropierea lină de viteza țintă, independentă de numărul de cadre pe secundă.
            _speed += (_targetSpeed - _speed) * (1.0f - MathF.Exp(-SmoothK * dt));
            step = _speed * dt;

            if (_parkCabin >= 0 && MathF.Abs(_speed) < ParkStartSpeed)
            {
                StartParking();
            }
        }

        _angle += step;
        if (_park == null)
        {
            _angle = Wrap360(_angle);   // în timpul Tween-ului unghiul nu se "înfășoară" (From/To sunt fixe)
        }

        // 2. Viteza și accelerația unghiulară reale (pentru afișare și pentru leagăn).
        float omega = step / dt;
        _alpha = (omega - _omega) / dt;
        _omega = omega;

        UpdateSwing(dt);
        UpdateTransforms();
    }

    private void HandleInput(float dt, Input input)
    {
        float change = input.Axis(SpeedDown, SpeedUp);
        if (change != 0.0f)
        {
            CancelParking();
            _targetSpeed = Math.Clamp(_targetSpeed + change * SpeedChangeRate * dt, -MaxSpeed, MaxSpeed);
        }

        if (input.WasPressed(StopResume))
        {
            CancelParking();
            if (MathF.Abs(_targetSpeed) > 0.01f)
            {
                _resumeSpeed = _targetSpeed;
                _targetSpeed = 0.0f;
                ConsolePanel.PrintMessage("roata se oprește lin");
            }
            else
            {
                _targetSpeed = MathF.Abs(_resumeSpeed) > 0.01f ? _resumeSpeed : 15.0f;
                ConsolePanel.PrintMessage($"roata pornește: {_targetSpeed:0.0}°/s");
            }
        }

        for (int k = 0; k < CabinCount; k++)
        {
            if (input.WasPressed(BringCabin[k]))
            {
                RequestParking(k);
            }
        }
    }

    // --- Aducerea unei cabine la peron (Tween pe unghiul roții) ------------------------------

    private void RequestParking(int cabin)
    {
        if (MathF.Abs(_targetSpeed) > 0.01f)
        {
            _resumeSpeed = _targetSpeed;
        }
        _targetSpeed = 0.0f;
        _parkCabin = cabin;

        if (_park != null)
        {
            // Un Tween era deja în curs: pornim unul nou din unghiul curent spre noua cabină.
            _park = null;
            StartParking();
        }
        else if (MathF.Abs(_speed) < ParkStartSpeed)
        {
            StartParking();
        }
        else
        {
            ConsolePanel.PrintMessage($"cabina {cabin + 1}: roata frânează, apoi aduce cabina la peron");
        }
    }

    private void StartParking()
    {
        // Cabina i este la peron (jos, la -90°) când CabinAngle(i) + unghi = -90°, adică unghi = -i · 45°.
        float target = -_parkCabin * CabinStep;
        float delta = Wrap180(target - _angle);   // drumul cel mai scurt: cel mult 180° într-un sens sau altul
        _speed = 0.0f;

        if (MathF.Abs(delta) < 0.01f)
        {
            ConsolePanel.PrintMessage($"cabina {_parkCabin + 1} este deja la peron");
            _parkCabin = -1;
            return;
        }

        float duration = 0.8f + 2.4f * MathF.Abs(delta) / 180.0f;   // drum mai lung = durată mai mare
        _park = new Tween(new Vector3(_angle, 0.0f, 0.0f), new Vector3(_angle + delta, 0.0f, 0.0f),
                          duration, Easing.EaseInOut);
        ConsolePanel.PrintMessage($"cabina {_parkCabin + 1} vine la peron: {delta:+0.0;-0.0}° în {duration:0.00} s");
    }

    private void CancelParking()
    {
        if (_park != null)
        {
            _speed = _omega;   // roata continuă lin cu viteza pe care o avea în Tween
            _park = null;
        }
        _parkCabin = -1;
    }

    // --- Leagănul cabinelor: resort amortizat (bonus) ----------------------------------------

    private void UpdateSwing(float dt)
    {
        const float degToRad = MathF.PI / 180.0f;
        float omega0 = MathF.Sqrt(Gravity / HangLength);   // pulsația proprie a pendulului
        float alpha = _alpha * degToRad;                    // rad/s²
        float maxSwing = MaxSwing * degToRad;

        for (int i = 0; i < CabinCount; i++)
        {
            // Accelerația punctului de prindere pe orizontală: componenta X a accelerației tangențiale,
            // α · R · (-sin θ). Cabina "rămâne în urmă" față de ea.
            float theta = (CabinAngle(i) + _angle) * degToRad;
            float ax = -SwingStrength * alpha * Radius * MathF.Sin(theta);

            // φ'' = -ω0² φ - 2ζω0 φ' - a_x / L   (Euler semi-implicit: întâi viteza, apoi unghiul)
            float acceleration = -omega0 * omega0 * _swing[i] - 2.0f * SwingDamping * omega0 * _swingSpeed[i] - ax / HangLength;
            _swingSpeed[i] += acceleration * dt;
            _swing[i] = Math.Clamp(_swing[i] + _swingSpeed[i] * dt, -maxSwing, maxSwing);
        }
    }

    // --- Transformări ---------------------------------------------------------------------------

    private void UpdateTransforms()
    {
        _hub.Transform.Rotation = new Vector3(0.0f, 0.0f, _angle);
        for (int i = 0; i < CabinCount; i++)
        {
            // Rotația inversă față de roată (rămâne verticală) + leagănul.
            float swingDegrees = _swing[i] * 180.0f / MathF.PI;
            _cabins[i].Transform.Rotation = new Vector3(0.0f, 0.0f, -_angle + swingDegrees);
        }
    }

    // --- Funcții ajutătoare ---------------------------------------------------------------------

    /// <summary>Unghiul cabinei i pe roată (față de butuc): cabina 1 jos (-90°), apoi în sens trigonometric.</summary>
    private static float CabinAngle(int i) => i * CabinStep - 90.0f;

    private static Vector3 PointOnRim(int i)
    {
        float a = CabinAngle(i) * MathF.PI / 180.0f;
        return new Vector3(Radius * MathF.Cos(a), Radius * MathF.Sin(a), 0.0f);
    }

    /// <summary>Cabina cea mai apropiată de peron (de poziția de jos).</summary>
    private int NearestCabin()
    {
        int best = 0;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < CabinCount; i++)
        {
            float distance = MathF.Abs(Wrap180(CabinAngle(i) + _angle + 90.0f));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        return best;
    }

    /// <summary>Aduce un unghi în [0, 360).</summary>
    private static float Wrap360(float degrees)
    {
        float r = degrees % 360.0f;
        return r < 0.0f ? r + 360.0f : r;
    }

    /// <summary>Aduce un unghi în [-180, 180).</summary>
    private static float Wrap180(float degrees) => Wrap360(degrees + 180.0f) - 180.0f;

    public override IEnumerable<string> Describe()
    {
        foreach (string line in base.Describe()) yield return line;

        float maxSwing = _swing.Max(s => MathF.Abs(s)) * 180.0f / MathF.PI;
        string park = _park != null ? $"Tween {_park.Progress:P0} din {_park.Duration:0.00} s (EaseInOut)"
                    : _parkCabin >= 0 ? "frânează înainte de Tween"
                    : "nu";
        yield return $"Roata          : unghi {Wrap360(_angle):0.0}°   v = {_omega:0.00}°/s   " +
                     $"țintă {_targetSpeed:0.00}°/s   k = {SmoothK}   α = {_alpha:0.0}°/s²";
        yield return $"Peron          : cabina {NearestCabin() + 1}   cerută: " +
                     $"{(_parkCabin >= 0 ? (_parkCabin + 1).ToString() : "-")}   aducere: {park}";
        yield return $"Cabine         : {CabinCount}, verticale (rotație -unghi față de butuc)   " +
                     $"leagăn maxim {maxSwing:0.0}°";
        yield return $"Ierarhie       : butuc (pivot) → spițe, jantă ({RimSegments} segmente × 2), bare, 8 pivoți de cabină → forme";
    }
}
