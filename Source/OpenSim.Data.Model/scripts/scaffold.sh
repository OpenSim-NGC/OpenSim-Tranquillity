# dotnet user-secrets init
# dotnet user-secrets set ConnectionStrings:IdentityDevelopment 'server=mdickson-linux.home; port=3306; database=identity; user=opensim; password=*M1ke.Chase*' 
# dotnet user-secrets set ConnectionStrings:SearchDevelopment 'server=mdickson-linux.home; port=3306; database=ossearch; user=opensim; password=*M1ke.Chase*' 
# dotnet user-secrets set ConnectionStrings:EconomyDevelopment 'server=mdickson-linux.home; port=3306; database=osmoney; user=opensim; password=*M1ke.Chase*' 
# dotnet user-secrets set ConnectionStrings:CoreDevelopment 'server=mdickson-linux.home; port=3306; database=opensim; user=opensim; password=*M1ke.Chase*' 
# dotnet user-secrets set ConnectionStrings:RegionDevelopment 'server=mdickson-linux.home; port=3306; database=opensim; user=opensim; password=*M1ke.Chase*' 

dotnet ef dbcontext scaffold 'server=mdickson-linux.home; port=3306; database=identity; user=opensim; password=*M1ke.Chase*; GuidFormat=None' Microting.EntityFrameworkCore.MySql \
	-f --no-onconfiguring \
	--context IdentityContext \
	--schema identity \
	-o Models/Identity

dotnet ef dbcontext scaffold 'server=mdickson-linux.home; port=3306; database=ossearch; user=opensim; password=*M1ke.Chase*; GuidFormat=None' Microting.EntityFrameworkCore.MySql \
	-f --no-onconfiguring \
	--context OpenSimSearchContext \
	--schema ossearch \
	-o Models/Search

dotnet ef dbcontext scaffold 'server=mdickson-linux.home; port=3306; database=osmoney; user=opensim; password=*M1ke.Chase*; GuidFormat=None' Microting.EntityFrameworkCore.MySql \
	-f --no-onconfiguring \
	--context OpenSimEconomyContext \
	--schema osmoney \
	-o Models/Economy

dotnet ef dbcontext scaffold 'server=mdickson-linux.home; port=3306; database=opensim; user=opensim; password=*M1ke.Chase*; GuidFormat=None' Microting.EntityFrameworkCore.MySql \
	-f --no-onconfiguring \
	--context OpenSimCoreContext \
	--schema opensim \
	-o Models/Core

dotnet ef dbcontext scaffold 'server=mdickson-linux.home; port=3306; database=opensim; user=opensim; password=*M1ke.Chase*; GuidFormat=None' Microting.EntityFrameworkCore.MySql \
	-f --no-onconfiguring \
	--context OpenSimRegionContext \
	--schema opensim \
	-o Models/Region \
	-t bakedterrain \
	-t land \
	-t landaccesslist \
	-t migrations \
	-t primitems \
	-t prims \
	-t primshapes \
	-t regionban \
	-t regionenvironment \
	-t regionextra \
	-t regionsettings \
	-t regionwindlight \
	-t spawn_points \
	-t terrain 
