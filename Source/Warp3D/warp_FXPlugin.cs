using System;
using System.Collections.Generic;
using System.Text;

namespace Warp3D
{
    public abstract class warp_FXPlugin
    {
        public warp_Scene scene = null;
        public warp_Screen screen = null;

        public warp_FXPlugin( warp_Scene scene )
		{
			this.scene = scene;
			screen = scene.renderPipeline.screen;
		}
		
		public abstract void apply();
    }
}
