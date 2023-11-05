using Godot;
using System;
using System.Collections.Generic;

public partial class cube : Node3D
{
	// Called when the node enters the scene tree for the first time.
	Node3D ports;
	Node3D portTemplate;
	public override void _Ready()
	{
		ports = GetNode<Node3D>("ports");
		portTemplate = GetNode<Node3D>("ports/port");
		var angles = new List<Vector3> {
			new Vector3(90f,0f,0f),
			new Vector3(-90f,0f,0f),
			new Vector3(0f,0f,90f),
			new Vector3(0f,0f,-90f),
			new Vector3(180f,0f,0f),
		};
		foreach (Vector3 angle in angles) {
			var newport = portTemplate.Duplicate() as Node3D;
			ports.AddChild(newport);
			newport.RotationDegrees = angle;
		}
	}

	// Called every frame. 'delta' is the elapsed time since the previous frame.
	//public override void _Process(double delta)
	//{
		//if (ports is null) return;
		//foreach (Node3D port in ports.GetChildren()) {
			//port.GetChild<MeshInstance3D>(0).Transparency = Transparency;
		//}
	//}
}
