using Godot;
using System;
using System.IO; // StreamReader
using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;

using RD = Godot.RenderingDevice;
using RDUsage = Godot.RenderingDevice.TextureUsageBits;

public class ConfigEntry
{
	public string name { get; set; }
	public string values { get; set; }
}

class Config {
	public List<int> dimensions, t0cells, t1cells, wires, owires, ot0cells, ot1cells;
	public int totalDimensions;
	public List<uint> computeDimensions;
	public bool clearBetweenUpdates;
	public int brainUpdates;
	public void recalculateDimensions() {
		var (x,y,z) = (this.dimensions[0]+2,this.dimensions[1]+2,this.dimensions[2]+2);
		this.computeDimensions = new List<uint> { (uint)(x/4 + Math.Min(1,x%4)), (uint)(y/4 + Math.Min(1,y%4)), (uint)(z/4 + Math.Min(1,z%4)) };
	}
	public float trace_fade_rate = 0.2f; // units of Brain Updates

	public float readyTransparency = 0f;
	public float readyTransparencyDefault = 0f;
	public float readyTransparencyAlternate = 0.9f;
}

public class Activation
{
	public List<int> data { get; set; }
}

public class ActivationMap : ClassMap<Activation>
{
    public ActivationMap()
    {
        Map(p => p.data).Index(0); 
    }
}

public enum IO {Input, Output, Hidden};
public class Cube{
	public List<Node3D> signals;
	public Node3D node;
	public IO kind;
	public int id;
	public int x,y,z;
	public Tween colorTween;
	public Tween signalTween;
}

public partial class main : Node3D
{
	//[StructLayout(LayoutKind.Sequential, Pack=16)]
	struct PushConstants {
		public int REPEATS;
		int fill1; // padding for 16 byte alignment glsl assumes
		int fill2; // padding
		int fill3; // padding
	}
	private int xzytoxyz(int i, int dx, int dy, int dz) {
		var x = i % dx;
		var z = i / dx % dz;
		var y = i / (dx*dz);
		var id = z*(dx*dy) + y*dx + x;
		return id;
	}
	private Config getConfigFromFile(string file) {
        var configuration = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Comment = '#',
            Delimiter = ",",
            //HasHeaderRecord = false
        };
		
		Dictionary<string, string> config;
		using (var reader = new StreamReader(file))
        using (var csv = new CsvReader(reader, configuration))
        {
            var records = csv.GetRecords<ConfigEntry>();
			config = records.ToDictionary(c => c.name, c => c.values);
        }
		Config result = new Config();
		result.dimensions = config["scale"].Split(',').Select(int.Parse).ToList();
		result.dimensions.Add(result.dimensions[0]*result.dimensions[1]*result.dimensions[2]); // 4th dimension is total size
		result.t0cells = config["T0_cells"].Split(',').Select(int.Parse).ToList();
		result.t1cells = config["T1_cells"].Split(',').Select(int.Parse).ToList();
		result.clearBetweenUpdates = ((int.Parse(config["clearBetweenUpdates"]) == 1) ? true : false);
		result.brainUpdates = int.Parse(config["brainUpdates"]);
		result.wires = config["wire_active"].Select(c => int.Parse(c.ToString())).ToList();
		result.owires = new List<int>(result.wires);
		result.ot0cells = new List<int>(result.t0cells);
		result.ot1cells = new List<int>(result.t1cells);
		result.recalculateDimensions();

		// cliff-xzy -> gpu-xyz conversion
		var oldwires = new List<int>(result.wires);
		foreach (int i in Enumerable.Range(0,result.wires.Count)) result.wires[i] = 0;
		foreach (int i in Enumerable.Range(0,oldwires.Count)) {
			if (oldwires[i] != 1) continue;
			var newloc = xzytoxyz(i, result.dimensions[0], result.dimensions[1], result.dimensions[2]);
			result.owires[i] = 1;
			result.wires[newloc] = 1;
		}
		GD.Print("Input Mapping");
		foreach (int i in Enumerable.Range(0,result.t0cells.Count)) {
			result.ot0cells[i] = result.t0cells[i];
			result.t0cells[i] = xzytoxyz(result.t0cells[i], result.dimensions[0], result.dimensions[1], result.dimensions[2]);
			GD.Print($"{result.ot0cells[i]} -> {result.t0cells[i]}");
		}
		foreach (int i in Enumerable.Range(0,result.t1cells.Count)) {
			result.ot1cells[i] = result.t1cells[i];
			result.t1cells[i] = xzytoxyz(result.t1cells[i], result.dimensions[0], result.dimensions[1], result.dimensions[2]);
		}

		return result;
	}

	private List<List<int>> getActivationsFromFile(string file) {
        var configuration = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Comment = '#',
            Delimiter = ",",
            HasHeaderRecord = false
        };
		using (var reader = new StreamReader(file))
		using (var csv = new CsvReader(reader, configuration))
		{
			csv.Context.RegisterClassMap<ActivationMap>();
			var records = csv.GetRecords<Activation>();
			return records.Select(row => row.data).ToList();
		}
	}
	private void removeTemporaries() {
		foreach (Node3D node in FindChildren("", "Node3D")) {
			if (node.FindChild("mesh") == null) continue;
			node.QueueFree();
		}
	}
	PackedScene Cube;
	private void setRaw(ref byte[] arr, int x, int y, int z, int v) {
		arr[z*(config.dimensions[0]+2)*(config.dimensions[1]+2) + y*(config.dimensions[0]+2) + x] = (byte)v;
	}
	private int readRaw(ref byte[] arr, int x, int y, int z) {
		return arr[z*(config.dimensions[0]+2)*(config.dimensions[1]+2) + y*(config.dimensions[0]+2) + x];
	}
	private void set(ref byte[] arr, int x, int y, int z, int v) {
		arr[(z+1)*(config.dimensions[0]+2)*(config.dimensions[1]+2) + y*(config.dimensions[0]+2) + (config.dimensions[0]+2) + 1 + x] = (byte)v;
	}
	private int read(ref byte[] arr, int x, int y, int z) {
		return arr[(z+1)*(config.dimensions[0]+2)*(config.dimensions[1]+2) + y*(config.dimensions[0]+2) + (config.dimensions[0]+2) + 1 + x];
	}

	struct UInt32Vector3 {
		public UInt32 x;
		public UInt32 y;
		public UInt32 z;
		private UInt32 _; // padding
	}

	RenderingDevice rd;
	Rid pipeline, uniform_set_brain_update, cm_tex, amt0_tex, amt1_tex, n1_tex, n2_tex, ins_tex, outs_tex;
	byte[] cm;
	byte[] amt0;
	byte[] amt1;
	byte[] n1;
	byte[] n2;
	byte[] ins;
	byte[] outs;
	Stopwatch stopwatch;
	private void initPipeline() {
		rd = RenderingServer.CreateLocalRenderingDevice();
		var shaderBrainUpdateFile = ResourceLoader.Load("res://brain_update.glsl") as RDShaderFile;
		var shaderBrainUpdateSPIRV = shaderBrainUpdateFile.GetSpirV();
		var shaderBrainUpdate = rd.ShaderCreateFromSpirV(shaderBrainUpdateSPIRV);

		var totalSize = (config.dimensions[0]+2)*(config.dimensions[1]+2)*(config.dimensions[2]+2);
		cm = new byte[totalSize];
		Array.Clear(cm);

		amt0 = new byte[totalSize];
		Array.Clear(amt0);

		amt1 = new byte[totalSize];
		Array.Clear(amt1);

		// information about where activations
		// came from in cartesian space (all 26 directions)
		// supporting 2 neighbors so far
		n1 = new byte[totalSize]; // neighbor 1
		Array.Clear(n1);
		n2 = new byte[totalSize]; // neighbor 1
		Array.Clear(n2);

		// which cells are inputs & which are outputs
		ins = new byte[totalSize];
		Array.Clear(ins);
		outs = new byte[totalSize];
		Array.Clear(outs);

		foreach (int i in Enumerable.Range(0,config.wires.Count)) {
			var x = i % config.dimensions[0];
			var y = i / config.dimensions[0] % config.dimensions[1];
			var z = i / (config.dimensions[0]*config.dimensions[1]);
			if (config.wires[i] == 1) {       set(ref cm,   x,y,z, 1); }
			if (config.t0cells.Contains(i)) { set(ref ins,  x,y,z, 1); }
			if (config.t1cells.Contains(i)) { set(ref outs, x,y,z, 1); }
		}

		// *********** textures & uniforms
		var r_fmt = new RDTextureFormat();
		r_fmt.Width = (uint)config.dimensions[0]+2;
		r_fmt.Height = (uint)config.dimensions[1]+2;
		r_fmt.Depth = (uint)config.dimensions[2]+2;
		r_fmt.TextureType = RD.TextureType.Type3D;
		r_fmt.UsageBits = RDUsage.StorageBit | RDUsage.CanUpdateBit | RDUsage.CanCopyFromBit;
		r_fmt.Format = RD.DataFormat.R8Uint;
		

		// cm
		cm_tex = rd.TextureCreate(r_fmt, new RDTextureView(), new Godot.Collections.Array<byte[]> {cm});
		var cm_uniform = new RDUniform();
		cm_uniform.UniformType = RD.UniformType.Image;
		cm_uniform.Binding = 0;
		cm_uniform.AddId(cm_tex);

		// amt0
		amt0_tex = rd.TextureCreate(r_fmt, new RDTextureView(), new Godot.Collections.Array<byte[]> {amt0});
		var amt0_uniform = new RDUniform();
		amt0_uniform.UniformType = RD.UniformType.Image;
		amt0_uniform.Binding = 1;
		amt0_uniform.AddId(amt0_tex);

		// amt1
		amt1_tex = rd.TextureCreate(r_fmt, new RDTextureView(), new Godot.Collections.Array<byte[]> {amt1});
		var amt1_uniform = new RDUniform();
		amt1_uniform.UniformType = RD.UniformType.Image;
		amt1_uniform.Binding = 2;
		amt1_uniform.AddId(amt1_tex);

		// n1
		n1_tex = rd.TextureCreate(r_fmt, new RDTextureView(), new Godot.Collections.Array<byte[]> {n1});
		var n1_uniform = new RDUniform();
		n1_uniform.UniformType = RD.UniformType.Image;
		n1_uniform.Binding = 3;
		n1_uniform.AddId(n1_tex);

		// n2
		n2_tex = rd.TextureCreate(r_fmt, new RDTextureView(), new Godot.Collections.Array<byte[]> {n2});
		var n2_uniform = new RDUniform();
		n2_uniform.UniformType = RD.UniformType.Image;
		n2_uniform.Binding = 4;
		n2_uniform.AddId(n2_tex);

		// ins
		ins_tex = rd.TextureCreate(r_fmt, new RDTextureView(), new Godot.Collections.Array<byte[]> {ins});
		var ins_uniform = new RDUniform();
		ins_uniform.UniformType = RD.UniformType.Image;
		ins_uniform.Binding = 5;
		ins_uniform.AddId(ins_tex);

		// outs
		outs_tex = rd.TextureCreate(r_fmt, new RDTextureView(), new Godot.Collections.Array<byte[]> {outs});
		var outs_uniform = new RDUniform();
		outs_uniform.UniformType = RD.UniformType.Image;
		outs_uniform.Binding = 6;
		outs_uniform.AddId(outs_tex);

		// calculate the actual dimensions of the padded matrix, so the kernels know where to compute
		UInt32Vector3 dims = new UInt32Vector3{x=(uint)(config.dimensions[0]+2),y=(uint)(config.dimensions[1]+2),z=(uint)(config.dimensions[2]+2)};
		var dims_buffer = rd.UniformBufferCreate(16, MemoryMarshal.Cast<UInt32Vector3, byte>(MemoryMarshal.CreateSpan(ref dims, 1)).ToArray());
		var dims_uniform = new RDUniform();
		dims_uniform.UniformType = RD.UniformType.UniformBuffer;
		dims_uniform.Binding = 7;
		dims_uniform.AddId(dims_buffer);

		uniform_set_brain_update = rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> {cm_uniform, amt0_uniform, amt1_uniform, n1_uniform, n2_uniform, ins_uniform, outs_uniform, dims_uniform}, shaderBrainUpdate, 0);

		// create compute pipeline
		pipeline = rd.ComputePipelineCreate(shaderBrainUpdate);

        stopwatch = new Stopwatch();
	}

	Rid testmat_tex, uniform_set_test_gpu;
	byte[] testmat;
	private void initTestPipeline() {

		// TODO create config.dimensions
		Config cfg = new Config();
		cfg.dimensions = new List<int>{0, 0, 0, 0};
		//cfg.t0cells = config["T0_cells"].Split(',').Select(int.Parse).ToList();
		//cfg.t1cells = config["T1_cells"].Split(',').Select(int.Parse).ToList();
		//cfg.clearBetweenUpdates = ((int.Parse(config["clearBetweenUpdates"]) == 1) ? true : false);
		//cfg.brainUpdates = int.Parse(config["brainUpdates"]);
		//cfg.recalculateDimensions();
		config = cfg;

		rd = RenderingServer.CreateLocalRenderingDevice();
		var shaderTestUpdateFile = ResourceLoader.Load("res://test_gpu.glsl") as RDShaderFile;
		var shaderTestUpdateSPIRV = shaderTestUpdateFile.GetSpirV();
		var shaderTestUpdate = rd.ShaderCreateFromSpirV(shaderTestUpdateSPIRV);

		var totalSize = 2*2*2;
		testmat = new byte[totalSize];
		Array.Clear(testmat);

		setRaw(ref testmat, 0,0,0, 1);
		setRaw(ref testmat, 1,0,0, 1);
		setRaw(ref testmat, 0,0,1, 2);
		setRaw(ref testmat, 1,0,1, 2);
		setRaw(ref testmat, 0,1,0, 3);
		setRaw(ref testmat, 1,1,0, 3);
		setRaw(ref testmat, 0,1,1, 4);
		setRaw(ref testmat, 1,1,1, 4);


		// *********** textures & uniforms
		var r_fmt = new RDTextureFormat();
		r_fmt.Width = (uint)2;
		r_fmt.Height = (uint)2;
		r_fmt.Depth = (uint)2;
		r_fmt.TextureType = RD.TextureType.Type3D;
		r_fmt.UsageBits = RDUsage.StorageBit | RDUsage.CanUpdateBit | RDUsage.CanCopyFromBit;
		r_fmt.Format = RD.DataFormat.R8Uint;
		

		// testmat
		testmat_tex = rd.TextureCreate(r_fmt, new RDTextureView(), new Godot.Collections.Array<byte[]> {testmat});
		var testmat_uniform = new RDUniform();
		testmat_uniform.UniformType = RD.UniformType.Image;
		testmat_uniform.Binding = 0;
		testmat_uniform.AddId(testmat_tex);

		uniform_set_test_gpu = rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> {testmat_uniform}, shaderTestUpdate, 0);

		// create compute pipeline
		pipeline = rd.ComputePipelineCreate(shaderTestUpdate);
	}

	private void runTestPipeline() {
		var compute_list = rd.ComputeListBegin();
		rd.ComputeListBindComputePipeline(compute_list, pipeline);
		rd.ComputeListBindUniformSet(compute_list, uniform_set_test_gpu, 0);
		rd.ComputeListDispatch(compute_list, 2, 2, 2);
		rd.ComputeListEnd();
		rd.Submit();
		rd.Sync();

		testmat = rd.TextureGetData(testmat_tex, 0);
	}

	private void validateTestResults() {
		bool tests_failed = false;
		tests_failed |= readRaw(ref testmat, 0,0,0) != 1*1;
		tests_failed |= readRaw(ref testmat, 1,0,0) != 1*1;
		tests_failed |= readRaw(ref testmat, 0,0,1) != 2*2;
		tests_failed |= readRaw(ref testmat, 1,0,1) != 2*2;
		tests_failed |= readRaw(ref testmat, 0,1,0) != 3*3;
		tests_failed |= readRaw(ref testmat, 1,1,0) != 3*3;
		tests_failed |= readRaw(ref testmat, 0,1,1) != 4*4;
		tests_failed |= readRaw(ref testmat, 1,1,1) != 4*4;
		if (tests_failed) {
			GD.Print("The GLSL kernels do not appear to be working correctly. This might mean you don't have the right GPU drivers installed.");
		} else {
			GD.Print("All tests passed okay. It appears the GPU drivers are working correctly, and the GLSL kernels perform parallel computation okay.");
		}
	} 

	private GodotObject camera;
	List<List<List<Node3D>>> nodes;
	List<Node3D> allnodes;
	List<Cube> wnodes;
	List<Cube> inodes;
	[Export] Color ColorRefractory {get; set;} = new Color(0.9f, 0.9f, 0.9f, 1f);
	[Export] Color ColorUnused {get; set;} = new Color(0.7f, 0.7f, 0.7f, 0.9f);
	[Export] Color ColorReady {get; set;} = new Color(0.3f, 0.74f, 2.0f, 1f);
	[Export] Color ColorOn {get; set;} = new Color(0.3f, 0.74f, 2.0f, 1f);
	[Export] Color ColorInput {get; set;} = new Color(0.18f, 0.98f, 0.18f, 1f);

	[Export] Color EmissionRefractory {get; set;} = new Color(0.9f, 0.9f, 0.9f, 1f);
	[Export] Color EmissionUnused {get; set;} = new Color(0.7f, 0.7f, 0.7f, 1f);
	[Export] Color EmissionReady {get; set;} = new Color(0.3f, 0.74f, 2.0f, 1f);
	[Export] Color EmissionOn {get; set;} = new Color(0.3f, 0.74f, 2.0f, 1f);
	[Export] Color EmissionInput {get; set;} = new Color(0.18f, 0.98f, 0.18f, 1f);

	[Export(PropertyHint.Range, "0.01, 100, 0.01")] float Speed {get; set;} = 1.0f;
	float SpeedSaved = 1.0f;
	bool Paused = false;
	bool SpeedToggle = false;
	[Export(PropertyHint.Range, "0.01, 100, 0.01")] float TimeScale {get; set;} = 1.0f;
	[Export] bool ShowSignals {get; set;} = false;
	[Export] bool ShowTraces {get; set;} = false;
	[Export] bool ShowIDs {get; set;} = false;
	[Export] bool ShowIcons {get; set;} = false;
	[Export] bool ShowCubes {get; set;} = true;
	[Export] bool MoveRotateSlowly {get; set;} = false;
	List<Vector3> directions;
	List<float> lengths;
	Tween helpTween, toastTween;
	Config config;
	List<List<int>> activations;
	int timestep = 0;
	int subtimestep = -1;
	// Called when the node enters the scene tree for the first time.
	public static List<int> Shuffle(List<int> list)
	{
		var random = new Random();
		var newShuffledList = new List<int>();
		var listcCount = list.Count;
		for (int i = 0; i < listcCount; i++)
		{
			var randomElementInList = random.Next(0, list.Count);
			newShuffledList.Add(list[randomElementInList]);
			list.Remove(list[randomElementInList]);
		}
		return newShuffledList;
	}
	public override void _Ready()
	{
		var args = OS.GetCmdlineUserArgs();

		if (args.Length == 0) {
			GD.Print();
			GD.Print("Usage:");
			GD.Print("  ./exec [options] -- config.csv activations.csv");
			GD.Print();
			GD.Print("Options:");
			GD.Print("  --fullscreen");
			GD.Print("  --write-movie <filename.avi>");
			GD.Print();
			GetTree().Quit();
			return;
		}

		if (args[0] == "test") {
			GD.Print("running gpu capabilities test...");
			SetProcess(false);
			SetPhysicsProcess(false);
			initTestPipeline();
			runTestPipeline();
			validateTestResults();
			GetTree().Quit();
			return;
		}
		GD.Print("Shouldn't be here...");

		if (args.Length < 2) {
			GD.Print();
			GD.Print("Error: pass config and activations files like this:");
			GD.Print("       usage:");
			GD.Print("       ./exec -- config.csv activations.csv");
			GD.Print();
			GetTree().Quit();
			return;
		}

		var config_file = args[0];
		var activations_file = args[1];
		string nonexisting_file = "";
		if (! Godot.FileAccess.FileExists(config_file)) nonexisting_file = config_file;
		if (! Godot.FileAccess.FileExists(activations_file)) nonexisting_file = activations_file;
		if (nonexisting_file.Length != 0) {
			GD.Print();
			GD.Print($"Error: file not found '{config_file}'");
			GD.Print("       usage:");
			GD.Print("       ./exec -- config.csv activations.csv");
			GD.Print();
			GetTree().Quit();
			return;
		}

		Cube = ResourceLoader.Load("res://cube.tscn") as PackedScene;

		removeTemporaries();

		config = getConfigFromFile(config_file);
		activations = getActivationsFromFile(activations_file);

		initPipeline();

		// Create Nodes
		nodes = new List<List<List<Node3D>>>();
		allnodes = new List<Node3D>();
		wnodes = new List<Cube>();
		inodes = new List<Cube>();
		GD.Print(string.Join(',',config.dimensions));
		Node3D root = GetNode<Node3D>("cubes");
		float half = config.dimensions[0] / 2f - 0.5f;
		for (int x=0; x<config.dimensions[0]; x++) {
            List<List<Node3D>> slice = new List<List<Node3D>>();
			for (int y=0; y<config.dimensions[1]; y++) {
                List<Node3D> row = new List<Node3D>();
				for (int z=0; z<config.dimensions[2]; z++) {
					var id = z*config.dimensions[0]*config.dimensions[1] + y*config.dimensions[0] + x;
					var node = Cube.Instantiate() as Node3D;
					node.GetNode<MeshInstance3D>("mesh").Mesh.SurfaceSetMaterial(0, new StandardMaterial3D());
					node.GetNode<Label3D>("Label3D").Text = $"{id}: ({x},{y},{z})";
					root.AddChild(node);
					allnodes.Add(node);
					float size = 1.0f;
					// placement
					node.Translate(new Vector3(x*size-half,y*size-half,-z*size+half));
					row.Add(node);
					if ((read(ref cm, x, y, z) == 1) || (read(ref ins,x,y,z) == 1)) {
						var cube = new Cube {node = node, signals = new List<Node3D>(), kind = IO.Hidden, x = x, y = y, z = z, id = id, colorTween = null};
						var isInputNode = config.t0cells.Contains(id);
						var isOutputNode = config.t1cells.Contains(id);
						if (isInputNode) {
							cube.kind = IO.Input;
							var occurrences = config.t0cells.FindAll(delegate(int d) {return d == id;}).Count;
							foreach (int _ in Enumerable.Range(0,occurrences)) {
								inodes.Add(cube);
							}
							node.GetNode<Sprite3D>("icons/outlet").Visible = true;
							//node.GetNode<Label3D>("Label3D").Text = $"{id}: ({x},{y},{z})";
						} else if (isOutputNode) {
							cube.kind = IO.Output;
							node.GetNode<Sprite3D>("icons/plug").Visible = true;
						} else {
						}
						wnodes.Add(cube); // TODO add Input/Output
					}
				}
				slice.Add(row);
			}
			nodes.Add(slice);
		}
		foreach (int i in Enumerable.Range(0,config.t0cells.Count)) {
			if (config.t0cells[i] == inodes[i].id) {
				continue;
			}
			//GD.Print($"did not find {config.t0cells[i]} at position {i}, found {inodes[i].id}");
			if (i == config.t0cells.Count - 1) {GD.Print("Error: inputs list something bad - nodes we created to represent inputs does not match up.");}
			int targeti = i+1;
			while (inodes[targeti].id != config.t0cells[i]) {++targeti;}
			var temp = inodes[i];
			inodes[i] = inodes[targeti];
			inodes[targeti] = temp;
		}

	lengths = new List<float> {
		0f, // (not used - 1-indexing)
		1.732f,// Left-Down-Forward
		1.4142f,// Forward-Down
		1.732f,// Right-Down-Forward
		1.4142f,// Forward-Left
		1f,// Forward
		1.4142f,// Forward-Right
		1.732f,// Left-Up-Forward
		1.4142f,// Forward-Up
		1.732f,// Right-Up-Forward

		1.4142f,// Left-Down
		1f,// Down
		1.4142f,// Right-Down
		1f,// Left
		1f,//          ( 0, 0,  0 ), // (origin - unused)
		1f,// Right
		1.4142f,// Left-Up
		1f,// Up
		1.4142f,// Right-Up

		1.732f,// Left-Down-Back
		1.4142f,// Back-Down
		1.732f,// Right-Down-Back
		1.4142f,// Back-Left
		1f,// Back
		1.4142f,// Back-Right
		1.732f,// Left-Up-Back
		1.4142f,// Back-Up
		1.732f,// Right-Up-Back
	};
	directions = new List<Vector3> {
		new Vector3 ( 9, 9, 9 ), // (buffer - unused - makes 1-count based)
		new Vector3 ( -1, -1, -1 ), // Left-Down-Forward
		new Vector3 ( 0, -1, -1 ), // Forward-Down
		new Vector3 ( 1, -1, -1 ), // Right-Down-Forward
		new Vector3 ( -1, 0, -1 ), // Forward-Left
		new Vector3 ( 0, 0, -1 ), // Forward
		new Vector3 ( 1, 0, -1 ), // Forward-Right
		new Vector3 ( -1, 1, -1 ), // Left-Up-Forward
		new Vector3 ( 0, 1, -1 ), // Forward-Up
		new Vector3 ( 1, 1, -1 ), // Right-Up-Forward

		new Vector3 ( -1, -1, 0 ), // Left-Down
		new Vector3 ( 0, -1, 0 ), // Down
		new Vector3 ( 1, -1, 0 ), // Right-Down
		new Vector3 ( -1, 0, 0 ), // Left
		new Vector3 ( 0, 0, 0), // (origin - unused)
		new Vector3 ( 1, 0, 0 ), // Right
		new Vector3 ( -1, 1, 0 ), // Left-Up
		new Vector3 ( 0, 1, 0 ), // Up
		new Vector3 ( 1, 1, 0 ), // Right-Up

		new Vector3 ( -1, -1, 1 ), // Left-Down-Back
		new Vector3 ( 0, -1, 1 ), // Back-Down
		new Vector3 ( 1, -1, 1 ), // Right-Down-Back
		new Vector3 ( -1, 0, 1 ), // Back-Left
		new Vector3 ( 0, 0, 1 ), // Back
		new Vector3 ( 1, 0, 1 ), // Back-Right
		new Vector3 ( -1, 1, 1 ), // Left-Up-Back
		new Vector3 ( 0, 1, 1 ), // Back-Up
		new Vector3 ( 1, 1, 1 ), // Right-Up-Back
	};
	for (int i=0; i<directions.Count; ++i) {
		//directions[i] = new Vector3(directions[i].X, directions[i].Z, -directions[i].Y);
		directions[i] = new Vector3(directions[i].X, directions[i].Y, -directions[i].Z);
	}

		// Create Signals
		var refsignal = GetNode<Node3D>("Signal");
		foreach (Cube cube in wnodes) {
			//if (cube.kind != IO.Hidden) continue;
			var pos = cube.node.Position;
			foreach (int i in Enumerable.Range(0,2)) {
				var signal = refsignal.Duplicate() as Node3D;
				cube.node.AddChild(signal);
				signal.Position = new Vector3(0,0,0);
				cube.signals.Add(signal);
			}
		}

		root.Translate(new Vector3(half, half, half));

		GetNode<Node3D>("camera_focus").Translate(new Vector3(config.dimensions[0]/2-0.5f, config.dimensions[1]/2-0.5f, config.dimensions[2]/2-0.5f));
		GetNode<Camera3D>("camera_focus/camera").Translate(new Vector3(0,0,config.dimensions[2]+2));//config.dimensions[2]/2*1.5f));

		threadBrainUpdate = new GodotThread();
		mutexBrainData = new Mutex();
		brainUpdateTween = CreateTween().SetLoops();
		brainUpdateTween.TweenCallback(Godot.Callable.From(this.invokeBrainUpdate)).SetDelay(1);

        foreach (int z in Enumerable.Range(0, config.dimensions[2]))
        {
            foreach (int y in Enumerable.Range(0, config.dimensions[1]))
            {
                foreach (int x in Enumerable.Range(0, config.dimensions[0]))
                {
					Node3D n = nodes[x][y][z];
					var mat = n.GetNode<MeshInstance3D>("mesh").Mesh.SurfaceGetMaterial(0) as StandardMaterial3D;
					if (read(ref cm, x,y,z) != 1) {
						// remove from visuals
						mat.AlbedoColor = ColorUnused;

						//n.Transparency = 0.88f;
						//n.Transparency = 0.99f;
						n.Visible = false;
					} else {
						// set default materials for all other nodes
						mat.AlbedoColor = ColorReady;
						mat.EmissionEnabled = true;
						mat.Emission = EmissionReady;
						//n.Transparency = config.readyTransparency;
						n.GetNode<MeshInstance3D>("mesh").Transparency = config.readyTransparency;
					}
                }
            }
        }
		GD.Print("Timestep 0");
		PauseToggle();
	}

	Tween brainUpdateTween; // calls BrainUpdate() every 1/Speed seconds
	int counter = 0;
	GodotThread threadBrainUpdate;
	Mutex mutexBrainData;
	bool isRunningBrainUpdate = false;
	bool ranBrainUpdate = false;
	private void invokeBrainUpdate() {
		mutexBrainData.Lock();
		var doupdate = ! isRunningBrainUpdate;
		mutexBrainData.Unlock();
		if (doupdate) {
			if (threadBrainUpdate.IsStarted()) threadBrainUpdate.WaitToFinish();
			mutexBrainData.Lock();
			isRunningBrainUpdate = true;
			ranBrainUpdate = false;
			mutexBrainData.Unlock();
			threadBrainUpdate.Start(Godot.Callable.From(this.BrainUpdate));
		}
	}
	private void BrainUpdate() {
		stopwatch.Reset();
		stopwatch.Start();


		if (subtimestep == -1) {
			mutexBrainData.Lock();
			if (config.clearBetweenUpdates){
				Array.Clear(amt0);
			}
			foreach (int i in Enumerable.Range(0,inodes.Count)) {
				var node = inodes[i];
				if (activations[timestep][i] >= 0)
					set(ref amt0, node.x, node.y, node.z, activations[timestep][i]);
				else {
					set(ref amt0, node.x, node.y, node.z, 9);
				}
			}
			if (config.clearBetweenUpdates){
				GD.Print("clearing");
				rd.TextureUpdate(amt0_tex, 0, amt0);
			}
			mutexBrainData.Unlock();
		}


		if (subtimestep >= 0) {
		rd.TextureUpdate(amt0_tex, 0, amt0);

		var compute_list = rd.ComputeListBegin();
		rd.ComputeListBindComputePipeline(compute_list, pipeline);
		rd.ComputeListBindUniformSet(compute_list, uniform_set_brain_update, 0);
		rd.ComputeListDispatch(compute_list, config.computeDimensions[0], config.computeDimensions[1], config.computeDimensions[2]);
		//rd.ComputeListDispatch(compute_list, 2, 2, 2);
		rd.ComputeListEnd();
		rd.Submit();
		rd.Sync();

		cm = rd.TextureGetData(cm_tex, 0);
		amt0 = rd.TextureGetData(amt0_tex, 0);
		amt1 = rd.TextureGetData(amt1_tex, 0);
		n1 = rd.TextureGetData(n1_tex, 0);
		n2 = rd.TextureGetData(n2_tex, 0);
		}

		mutexBrainData.Lock();
		isRunningBrainUpdate = false;
		ranBrainUpdate = true;
		mutexBrainData.Unlock();
	}

	// Called every frame. 'delta' is the elapsed time since the previous frame.
	public override void _Process(double delta)
	{
		if (MoveRotateSlowly)
			GetNode<Node3D>("camera_focus").RotateY(0.001f);

		// update visuals if GPU returned new data
		mutexBrainData.Lock();
		var haveNewData = ranBrainUpdate;
		mutexBrainData.Unlock();
		if (haveNewData) {
			mutexBrainData.Lock();
			var brain_update_label = GetNode<Label>("ui/brain_update_panel/label");
			brain_update_label.Text = $"update: {timestep}.{subtimestep}";
			ranBrainUpdate = false;
			int x,y,z;
			foreach (Cube cube in wnodes) {
				x = cube.x;
				y = cube.y;
				z = cube.z;
				var neighbor1 = read(ref n1, x,y,z);
				var neighbor2 = read(ref n2, x,y,z);
				var activity = read(ref amt0, x,y,z);
				if ((ShowSignals || ShowTraces) && (neighbor1 != 0) &&(activity == 9)) {
					if (cube.signalTween is not null) cube.signalTween.Kill();
					cube.signalTween = CreateTween().SetParallel(true);
				}
				if ((ShowSignals || ShowTraces) && (neighbor1 != 0) && (activity == 9)) {
					var forward = new Vector3(0,0,-1);
					var target = cube.signals[0].Position + directions[neighbor1];
					var right = directions[neighbor1].Cross(new Vector3(0,0,-1));
					if (right.Length() == 0) right = Vector3.Right;
					var up = directions[neighbor1].Cross(right);
					var length = lengths[neighbor1];
					target = cube.node.GlobalTransform.Origin + target;
					var ball = cube.signals[0].GetNode<Sprite3D>("ball");
					var trace = cube.signals[0].GetNode<MeshInstance3D>("arrow_shaft");
					cube.signals[0].LookAt(target, up);
					//cube.signals[0].Visible = true;
					if (ShowSignals) {
						ball.Visible = true;
						ball.Position = new Vector3(0,0,-length);
						cube.signalTween.TweenProperty(ball, "position", new Vector3(0,0,0), 1.0);
						cube.signalTween.TweenProperty(ball, "visible", false, 0).SetDelay(1.01);
					}
					if (ShowTraces) {
						trace.Visible = true;
						trace.Transparency = 0f;
						trace.Scale = new Vector3(trace.Scale.X,length,trace.Scale.Z);
						trace.Position = new Vector3(0,0,-length/2);
						cube.signalTween.TweenProperty(trace, "transparency", 1f, config.brainUpdates*config.trace_fade_rate);
						cube.signalTween.TweenProperty(trace, "visible", false, 0).SetDelay(config.brainUpdates*config.trace_fade_rate+0.01);
					}
					cube.signalTween.SetSpeedScale(Speed);
				}
				if ((ShowSignals || ShowTraces) && (neighbor2 != 0) && (activity == 9)) {
					var target = cube.signals[1].Position + directions[neighbor2];
					var right = directions[neighbor2].Cross(new Vector3(0,0,-1));
					if (right.Length() == 0) right = Vector3.Right;
					var up = directions[neighbor2].Cross(right);
					var length = lengths[neighbor2];
					target = cube.node.GlobalTransform.Origin + target;
					var ball = cube.signals[1].GetNode<Sprite3D>("ball");
					var trace = cube.signals[1].GetNode<MeshInstance3D>("arrow_shaft");
					cube.signals[1].LookAt(target, up);
					//cube.signals[0].Visible = true;
					if (ShowSignals) {
						ball.Visible = true;
						ball.Position = new Vector3(0,0,-length);
						cube.signalTween.TweenProperty(ball, "position", new Vector3(0,0,0), 1.0);
						cube.signalTween.TweenProperty(ball, "visible", false, 0).SetDelay(1.01);
					}
					if (ShowTraces) {
						trace.Visible = true;
						trace.Transparency = 0f;
						trace.Scale = new Vector3(trace.Scale.X,length,trace.Scale.Z);
						trace.Position = new Vector3(0,0,-length/2);
						cube.signalTween.TweenProperty(trace, "transparency", 1f, config.brainUpdates*config.trace_fade_rate);
						cube.signalTween.TweenProperty(trace, "visible", false, 0).SetDelay(config.brainUpdates*config.trace_fade_rate+0.01);
					}
					cube.signalTween.SetSpeedScale(Speed);
				}

				// todo
				if (cube.kind == IO.Hidden || cube.kind == IO.Input || cube.kind == IO.Output) {
					var mesh = cube.node.GetNode<MeshInstance3D>("mesh");
					StandardMaterial3D mat = mesh.GetActiveMaterial(0) as StandardMaterial3D;
					if (cube.colorTween is not null) cube.colorTween.Kill();
					cube.colorTween = CreateTween().SetParallel(true);
					var newActivity = read(ref amt0, x, y, z);
					switch (newActivity)
					{
						case 9:
							{
								cube.colorTween.TweenProperty(mat, "albedo_color", ColorOn, 0.1f/Speed); // dbg
								cube.colorTween.TweenProperty(mat, "emission", EmissionOn, 0.1f/Speed); // dbg
								cube.colorTween.TweenProperty(mesh, "transparency", 0f, 0.1f/Speed);
							}
							break;
						case 0:
							{
								cube.colorTween.TweenProperty(mat, "albedo_color", ColorReady, 0.1f/Speed);
								cube.colorTween.TweenProperty(mat, "emission", EmissionReady, 0.1f/Speed); // dbg
								cube.colorTween.TweenProperty(mesh, "transparency", config.readyTransparency, 0.1f/Speed);
							}
							break;
						default:
							{
								cube.colorTween.TweenProperty(mat, "albedo_color", ColorRefractory, 0.1f/Speed);
								cube.colorTween.TweenProperty(mat, "emission", EmissionRefractory, 0.1f/Speed); // dbg
								//cube.colorTween.TweenProperty(mesh, "transparency", 0.98f, 0.5f/Speed);
							}
							break;
					}
				}
			}
			
			++subtimestep;
			if (subtimestep == config.brainUpdates) {
				subtimestep = -1;
				++timestep;
				GD.Print($"Timestep {timestep}");
			}
			// stop updating if at the end of major and minor updates
			if (timestep == activations.Count) {
				brainUpdateTween.Stop();
			}
			stopwatch.Stop();
			//GD.Print($"GPU code elapsed: {stopwatch.ElapsedTicks / (double)Stopwatch.Frequency * 1000:#.00}ms");

			mutexBrainData.Unlock();
		}
	}

    public override void _PhysicsProcess(double delta)
    {
        base._PhysicsProcess(delta);
    }

	public void PauseToggle() {
		Paused = !Paused;
		if (Paused) {
			// SpeedSaved = Speed;
			// Speed = 0;
			//brainUpdateTimer.Paused = true;
			if (brainUpdateTween is not null) brainUpdateTween.SetSpeedScale(0);
			foreach (Cube cube in wnodes) {
				if (cube.colorTween is not null) cube.colorTween.SetSpeedScale(0);
				if (cube.signalTween is not null) cube.signalTween.SetSpeedScale(0);
			}
		} else {
			// Speed = SpeedSaved;
			//brainUpdateTimer.Paused = false;
			if (brainUpdateTween is not null) brainUpdateTween.SetSpeedScale(Speed);
			foreach (Cube cube in wnodes) {
				if (cube.colorTween is not null) cube.colorTween.SetSpeedScale(Speed);
				if (cube.signalTween is not null) cube.signalTween.SetSpeedScale(Speed);
			}
		}
	}

    public override void _Input(InputEvent @event)
    {
        // Check if the user presses the Escape key
		{
			if (@event is InputEventKey eventKey && eventKey.Pressed)
			{
				switch (eventKey.Keycode) {
					case Key.Escape:
						// Quit the game
						GetTree().Quit();
						break;
					case Key.A:
						if (config.readyTransparency == config.readyTransparencyDefault) {
							config.readyTransparency = config.readyTransparencyAlternate;
						} else {
							config.readyTransparency = config.readyTransparencyDefault;
						}
						foreach (Cube cube in wnodes) {
							cube.node.GetNode<MeshInstance3D>("mesh").Transparency = config.readyTransparency;
						}
						break;
					case Key.C:
						// Cubes vis
						ShowCubes = !ShowCubes;
						foreach (Cube cube in wnodes) {
							cube.node.GetNode<MeshInstance3D>("mesh").Visible = ShowCubes;
						}
						break;
					case Key.F:
						// Toggle Fullscreen
						DisplayServer.WindowSetMode(DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Fullscreen ? DisplayServer.WindowMode.Windowed : DisplayServer.WindowMode.Fullscreen);
						break;
					case Key.S:
						// Toggle Signals
						ShowSignals = !ShowSignals;
						if (ShowSignals == false) {
							foreach (Cube cube in wnodes) {
								cube.signals[0].GetNode<Sprite3D>("ball").Visible = false;
								cube.signals[1].GetNode<Sprite3D>("ball").Visible = false;
							}
						}
						break;
					case Key.T:
						// Traces
						ShowTraces = !ShowTraces;
						if (ShowTraces == false) {
							foreach (Cube cube in wnodes) {
								cube.signals[0].GetNode<MeshInstance3D>("arrow_shaft").Visible = false;
								cube.signals[1].GetNode<MeshInstance3D>("arrow_shaft").Visible = false;
							}
						}
						break;
					case Key.M:
						// Toggle Slow RotationMovement
						MoveRotateSlowly = !MoveRotateSlowly;
						break;
					case Key.D:
						// Toggle Slow RotationMovement
						ShowIDs = !ShowIDs;
						foreach (Node3D node in allnodes) {
							node.GetNode<Label3D>("Label3D").Visible = ShowIDs;
						}
						break;
					case Key.I:
						// Toggle Slow Icons
						ShowIcons = !ShowIcons;
						foreach (Node3D node in allnodes) {
							node.GetNode<Node3D>("icons").Visible = ShowIcons;
						}
						break;
					case Key.U:
						var brain_update_panel = GetNode<Control>("ui/brain_update_panel");
						brain_update_panel.Visible = !brain_update_panel.Visible;
						break;
					case Key.P:
						// Pause
						toastMessage("(paused)");
						PauseToggle();
						break;
				}
				if (eventKey.GetModifiersMask() == KeyModifierMask.MaskShift) {
					switch (eventKey.Keycode)
					{
						case Key.Slash: // ?
							// Show Help
							if (helpTween is not null) helpTween.Kill();
							helpTween = CreateTween().SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
							var help = GetNode<Control>("ui/help");
							help.Visible = true;
							help.Modulate = new Color(1, 1, 1, 1);
							helpTween.TweenProperty(help, "modulate", new Color(1, 1, 1, 0), 3);
							helpTween.Connect("finished", Godot.Callable.From(this.onHelpTweenFinished));
							break;
					}
				}
			}
		}
		if (@event is InputEventMouseButton) {
			var emb = @event as InputEventMouseButton;
			if (emb.GetModifiersMask() == KeyModifierMask.MaskShift) {
				var changed = false;
				if (emb.ButtonIndex == MouseButton.WheelUp){
					var delta = 0.1f;
					if (Speed >= 10) delta = 0.5f;
					if (Speed >= 20) delta = 1f;
					Speed += delta;
					changed = true;
				}
				if (emb.ButtonIndex == MouseButton.WheelDown){
					var delta = 0.1f;
					if (Speed >= 10) delta = 0.5f;
					if (Speed >= 20) delta = 1f;
					Speed = Math.Max(0, Speed -= delta);
					changed = true;
				}
				if (changed) {
					toastMessage($"Time: {Speed:0.#} x");
					if (!Paused) {
						if (brainUpdateTween is not null) brainUpdateTween.SetSpeedScale(Speed);
						foreach (Cube cube in wnodes) {
							if (cube.colorTween is not null) cube.colorTween.SetSpeedScale(Speed);
							if (cube.signalTween is not null) cube.signalTween.SetSpeedScale(Speed);
						}
					}
				}
			}
		}
    }

	private void toastMessage(string text) {
        var toast = GetNode<Control>("ui/toast_panel");
        toast.GetNode<Label>("label").Text = text;
        if (toastTween is not null) toastTween.Kill();
        toastTween = CreateTween().SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
        toast.Visible = true;
        toast.Modulate = new Color(1, 1, 1, 1);
        toastTween.TweenProperty(toast, "modulate", new Color(1, 1, 1, 0), 1 * TimeScale);
        toastTween.Connect("finished", Godot.Callable.From(this.onHelpTweenFinished));
	}

	public void onHelpTweenFinished() {
        var help = GetNode<Control>("ui/help");
		help.Visible = false;
	}
	public void onToastTweenFinished() {
        var toast = GetNode<Control>("ui/toast_panel");
		toast.Visible = false;
	}
}
